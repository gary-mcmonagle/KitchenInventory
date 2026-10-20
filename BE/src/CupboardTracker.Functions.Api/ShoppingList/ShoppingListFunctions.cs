using System.Globalization;
using System.Net;
using System.Text.Json;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Infrastructure.ShoppingList;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Functions.Api.ShoppingList;

/// <summary>The household's shopping list (#84). Every endpoint needs a signed-in user. See docs/api.md.</summary>
public sealed class ShoppingListFunctions(
    GetShoppingList get, AddToShoppingList add, ChangeShoppingListItem change, ClearDoneShoppingListItems clear, SuggestListItems suggest)
{
    private const int MaxBodyBytes = 4096;

    /// <summary><c>GET /api/shopping-list[?since=&lt;asOf&gt;]</c>.</summary>
    [Function("shoppingList")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shopping-list")] HttpRequestData request, CancellationToken cancellationToken)
    {
        var sinceText = request.Query["since"];
        DateTimeOffset? since = null;
        if (!string.IsNullOrEmpty(sinceText))
        {
            if (!DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "since must be a timestamp (the asOf of an earlier answer)." });
            }

            since = parsed;
        }

        var view = await get.ExecuteAsync(since, cancellationToken);
        return await JsonAsync(request, HttpStatusCode.OK, new { asOf = ShoppingListWire.Timestamp(view.AsOf), items = view.Items.Select(ShoppingListWire.Item) });
    }

    /// <summary><c>PUT /api/shopping-list/items/{id}</c> with <c>{ changeId, name, note?, productId?, source }</c>.</summary>
    [Function("shoppingListAdd")]
    public async Task<HttpResponseData> Add(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "shopping-list/items/{id:guid}")] HttpRequestData request,
        Guid id, FunctionContext context, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadAsync(request, cancellationToken);
        if (body is not { } root)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        Guid? productId = null;
        if (Text(root, "productId") is { } p)
        {
            if (!Guid.TryParse(p, out var parsed))
            {
                return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "productId must be a product ID." });
            }

            productId = parsed;
        }

        var result = await add.ExecuteAsync(
            new AddListItem(id, Text(root, "changeId") ?? "", Text(root, "name"), Text(root, "note"), productId, Text(root, "source") ?? ListItemSources.Typed),
            context.GetCurrentUser(), cancellationToken);
        return await RespondAsync(request, result);
    }

    /// <summary><c>POST /api/shopping-list/items/{id}/done</c> and <c>/undone</c> with <c>{ changeId }</c>.</summary>
    [Function("shoppingListTick")]
    public async Task<HttpResponseData> Tick(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shopping-list/items/{id:guid}/{state:regex(^(done|undone)$)}")] HttpRequestData request,
        Guid id, string state, FunctionContext context, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadAsync(request, cancellationToken);
        if (body is not { } root)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var changeId = Text(root, "changeId") ?? "";
        var result = state == "done"
            ? await change.DoneAsync(id, changeId, context.GetCurrentUser(), cancellationToken)
            : await change.UndoneAsync(id, changeId, cancellationToken);
        return await RespondAsync(request, result);
    }

    /// <summary><c>DELETE /api/shopping-list/items/{id}</c> with <c>{ changeId }</c>.</summary>
    [Function("shoppingListRemove")]
    public async Task<HttpResponseData> Remove(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "shopping-list/items/{id:guid}")] HttpRequestData request,
        Guid id, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadAsync(request, cancellationToken);
        if (body is not { } root)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        return await RespondAsync(request, await change.RemoveAsync(id, Text(root, "changeId") ?? "", cancellationToken));
    }

    /// <summary><c>POST /api/shopping-list/clear-done</c>: "Clear ticked".</summary>
    [Function("shoppingListClearDone")]
    public async Task<HttpResponseData> ClearDone(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shopping-list/clear-done")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var ids = await clear.ExecuteAsync(onlyOlderThanADay: false, cancellationToken);
        return await JsonAsync(request, HttpStatusCode.OK, new { cleared = ids });
    }

    /// <summary><c>GET /api/shopping-list/suggestions?q=</c>.</summary>
    [Function("shoppingListSuggestions")]
    public async Task<HttpResponseData> Suggestions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shopping-list/suggestions")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var q = request.Query["q"];
        if (q?.Length > ShoppingListNames.MaxName)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "q is too long." });
        }

        var suggestions = await suggest.ExecuteAsync(q, cancellationToken);
        return await JsonAsync(request, HttpStatusCode.OK, new
        {
            suggestions = suggestions.Select(s => new { name = s.Name, inStock = s.InStock, productId = s.ProductId }),
        });
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(ListChangeResult result) => result switch
    {
        ListChangeResult.Changed c => (HttpStatusCode.OK, new { item = ShoppingListWire.Item(c.Item), merged = c.Merged }),
        ListChangeResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        ListChangeResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such item on the list." }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static (JsonElement? Body, string? Error) Parse(byte[] body)
    {
        if (body.Length is 0 or > MaxBodyBytes)
        {
            return (null, "Body must be a JSON object.");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object ? (document.RootElement.Clone(), null) : (null, "Body must be a JSON object.");
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    private static async Task<(JsonElement? Body, string? Error)> ReadAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return Parse(buffer.ToArray());
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<HttpResponseData> RespondAsync(HttpRequestData request, ListChangeResult result)
    {
        var (status, body) = ToResponse(result);
        return await JsonAsync(request, status, body);
    }

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData request, HttpStatusCode status, object body)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, WebJson));
        return response;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}

/// <summary>Works out a typed item's aisle (#84) from the <c>list-aisles</c> queue.</summary>
public sealed partial class ListAisleFunction(AssignListAisle assign, ILogger<ListAisleFunction> logger)
{
    [Function("listAisle")]
    public async Task Run(
        [QueueTrigger(StorageQueueListAisleScheduler.QueueName, Connection = "AzureWebJobsStorage")] string message,
        CancellationToken cancellationToken)
    {
        Guid? itemId;
        try
        {
            itemId = StorageQueueListAisleScheduler.Deserialize(message);
        }
        catch (JsonException)
        {
            itemId = null;
        }

        if (itemId is not { } id)
        {
            LogUnreadable();
            return;
        }

        await assign.ExecuteAsync(id, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A list aisle message couldn't be read; dropped.")]
    private partial void LogUnreadable();
}

/// <summary>Every 15 minutes (#84): retries aisles Jev couldn't give, and clears items ticked off over a day ago.</summary>
public sealed class ShoppingListTimer(AssignListAisle aisles, ClearDoneShoppingListItems clear)
{
    [Function("shoppingListTimer")]
    public async Task Run([TimerTrigger("0 7,22,37,52 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        await aisles.RetryAsync(cancellationToken);
        await clear.ExecuteAsync(onlyOlderThanADay: true, cancellationToken);
    }
}
