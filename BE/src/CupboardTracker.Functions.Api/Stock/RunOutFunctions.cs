using System.Net;
using System.Text.Json;
using CupboardTracker.Application.RunOut;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>"Add to the shopping list when we run out" (#89): the setting for item detail, turning it on or off, and
/// the "Add to the shopping list?" sheet after a check-out. All need a signed-in user. See docs/api.md.</summary>
public sealed class RunOutFunctions(GetRunOutStatus status, SetAddWhenOut set, GetRunOutChoices choices)
{
    /// <summary><c>GET /api/products/{productId}/run-out</c> → <c>{ genericNameId, name, addWhenOut, inStock, canSet }</c>.</summary>
    [Function("productRunOut")]
    public async Task<HttpResponseData> Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId:guid}/run-out")] HttpRequestData request,
        Guid productId, CancellationToken cancellationToken)
    {
        var result = await status.ExecuteAsync(productId, cancellationToken);
        return result is null
            ? await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such known product." })
            : await JsonAsync(request, HttpStatusCode.OK, new
            {
                genericNameId = result.Id, name = result.Name, addWhenOut = result.AddWhenOut, inStock = result.InStock, canSet = result.CanSet,
            });
    }

    /// <summary><c>PUT /api/generic-names/{id}/add-when-out</c> with <c>{ "addWhenOut": true }</c>.</summary>
    [Function("setAddWhenOut")]
    public async Task<HttpResponseData> Set(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "generic-names/{id}/add-when-out")] HttpRequestData request,
        string id, FunctionContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        if (Parse(buffer.ToArray()) is not { } on)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Body must be { \"addWhenOut\": true|false }." });
        }

        var saved = await set.ExecuteAsync(Uri.UnescapeDataString(id), on, context.GetCurrentUser(), cancellationToken);
        return saved is null
            ? await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such generic name." })
            : await JsonAsync(request, HttpStatusCode.OK, new { genericNameId = saved.Id, name = saved.Name, addWhenOut = saved.AddWhenOut });
    }

    /// <summary><c>GET /api/run-out/batches/{batchId}</c>: the sheet for a tapped stock change push. 404 once the
    /// batch has expired (after 3 days).</summary>
    [Function("runOutForBatch")]
    public async Task<HttpResponseData> ForBatch(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "run-out/batches/{batchId:guid}")] HttpRequestData request,
        Guid batchId, FunctionContext context, CancellationToken cancellationToken)
    {
        var result = await choices.ForBatchAsync(batchId, context.GetCurrentUser(), cancellationToken);
        return result is null
            ? await JsonAsync(request, HttpStatusCode.NotFound, new { error = "That update has expired." })
            : await JsonAsync(request, HttpStatusCode.OK, ToBody(result));
    }

    /// <summary><c>GET /api/run-out/products/{productId}</c>: the sheet for a tapped "Checked out" toast.</summary>
    [Function("runOutForProduct")]
    public async Task<HttpResponseData> ForProduct(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "run-out/products/{productId:guid}")] HttpRequestData request,
        Guid productId, CancellationToken cancellationToken) =>
        await JsonAsync(request, HttpStatusCode.OK, ToBody(await choices.ForProductAsync(productId, cancellationToken)));

    internal static object ToBody(RunOutChoices result) => new
    {
        checkedOutBy = result.CheckedOutBy,
        at = result.At,
        items = result.Items.Select(i => new
        {
            productId = i.ProductId, genericNameId = i.GenericNameId, name = i.Name, left = i.Left, onList = i.OnList,
            addWhenOut = i.AddWhenOut, canSet = i.CanSet,
        }),
    };

    internal static bool? Parse(byte[] body)
    {
        if (body.Length is 0 or > 1024)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("addWhenOut", out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
