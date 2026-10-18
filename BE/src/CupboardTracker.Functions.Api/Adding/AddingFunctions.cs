using System.Globalization;
using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Adding;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Adding;

/// <summary>
/// Adding items from the app (#58): recognise a photo, list loose products to pick from, add to stock, and sort out a
/// store-label check-in. All need a signed-in user. See docs/api.md.
/// </summary>
public sealed class AddingFunctions(
    RecogniseProductPhoto recognise, GetLooseProducts looseProducts, AddToStock addToStock,
    AssignStoreLabel assignStoreLabel, DismissStoreLabelFromFeed dismissStoreLabel)
{
    private const int MaxBodyBytes = 16 * 1024;

    /// <summary><c>POST /api/photos/recognise</c> with a JPEG body (<c>Content-Type: image/jpeg</c>, at most 1 MB).
    /// The photo is never stored or logged.</summary>
    [Function("recognisePhoto")]
    public async Task<HttpResponseData> Recognise(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "photos/recognise")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var photo = await ReadAsync(request, RecogniseProductPhoto.MaxBytes, cancellationToken);
        if (photo is null)
        {
            return await JsonAsync(request, HttpStatusCode.RequestEntityTooLarge,
                new { error = $"Send one JPEG photo of at most {RecogniseProductPhoto.MaxBytes / 1024} KB." });
        }

        var (status, body) = ToResponse(await recognise.ExecuteAsync(photo.Value, cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>GET /api/products/loose</c>: the loose products to pick from, most recently added first.</summary>
    [Function("looseProducts")]
    public async Task<HttpResponseData> Loose(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/loose")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var products = await looseProducts.ExecuteAsync(cancellationToken);
        var response = await JsonAsync(request, HttpStatusCode.OK, new
        {
            products = products.Select(p => new
            {
                productId = p.ProductId,
                displayName = p.DisplayName,
                shelfLifeDays = p.ShelfLifeDays,
                lastAddedAt = Timestamp(p.LastAddedAt),
            }),
        });
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    /// <summary><c>POST /api/stock/add</c> with <c>{ requestId, items: [{ productId? | name, brand?, size?, quantity,
    /// source }] }</c>. Idempotent per <c>requestId</c>.</summary>
    [Function("addToStock")]
    public async Task<HttpResponseData> Add(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "stock/add")] HttpRequestData request,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var body = await ReadAsync(request, MaxBodyBytes, cancellationToken);
        var (requestId, items, error) = body is null ? (Guid.Empty, null, "Body is too large.") : ParseAdd(body.Value.Span);
        if (items is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error, errors = new Dictionary<string, string>() });
        }

        var (status, response) = ToResponse(await addToStock.ExecuteAsync(requestId, items, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, response);
    }

    /// <summary><c>POST /api/store-label-check-ins/{scanId}/assign</c> with <c>{ productId? | name, brand?, size?,
    /// source }</c>: says what a store-labelled check-in was, which adds its unit.</summary>
    [Function("assignStoreLabel")]
    public async Task<HttpResponseData> Assign(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "store-label-check-ins/{scanId:guid}/assign")] HttpRequestData request,
        Guid scanId,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var body = await ReadAsync(request, MaxBodyBytes, cancellationToken);
        var (item, error) = body is null ? (null, "Body is too large.") : ParseItem(body.Value.Span);
        if (item is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error, errors = new Dictionary<string, string>() });
        }

        var (status, response) = ToResponse(await assignStoreLabel.ExecuteAsync(scanId, item, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, response);
    }

    /// <summary><c>POST /api/scan-feed/store-labels/{scanId}/dismiss</c>: hide a store-label toast (it stays on the
    /// scanners screen).</summary>
    [Function("dismissStoreLabelFromFeed")]
    public async Task<HttpResponseData> DismissStoreLabel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "scan-feed/store-labels/{scanId:guid}/dismiss")] HttpRequestData request,
        Guid scanId,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var result = await dismissStoreLabel.ExecuteAsync(scanId, context.GetCurrentUser(), cancellationToken);
        return result switch
        {
            DismissStoreLabelResult.Dismissed d => await JsonAsync(request, HttpStatusCode.OK, new { scanId = d.ScanId, dismissed = true }),
            _ => await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such store-label check-in." }),
        };
    }

    internal static (Guid RequestId, IReadOnlyList<AddItem>? Items, string? Error) ParseAdd(ReadOnlySpan<byte> body)
    {
        const string Shape = "Body must be { \"requestId\": \"<uuid>\", \"items\": [ ... ] }.";
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var requestId)
                || requestId == Guid.Empty
                || !root.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return (Guid.Empty, null, Shape);
            }

            var items = new List<AddItem>();
            foreach (var element in array.EnumerateArray())
            {
                var (item, error) = ParseItem(element, defaultSource: null);
                if (item is null)
                {
                    return (Guid.Empty, null, error);
                }

                items.Add(item);
            }

            return (requestId, items, null);
        }
        catch (JsonException)
        {
            return (Guid.Empty, null, "Body must be a JSON object.");
        }
    }

    internal static (AddItem? Item, string? Error) ParseItem(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            return ParseItem(document.RootElement, defaultSource: AddSource.Pick);
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    private static (AddItem? Item, string? Error) ParseItem(JsonElement element, AddSource? defaultSource)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return (null, "Each item must be a JSON object.");
        }

        string? Text(string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        Guid? productId = null;
        if (element.TryGetProperty("productId", out var idValue) && idValue.ValueKind != JsonValueKind.Null)
        {
            if (idValue.ValueKind != JsonValueKind.String || !idValue.TryGetGuid(out var parsedId))
            {
                return (null, "productId must be a UUID.");
            }

            productId = parsedId;
        }

        var quantity = 1;
        if (element.TryGetProperty("quantity", out var q) && q.ValueKind != JsonValueKind.Null
            && (q.ValueKind != JsonValueKind.Number || !q.TryGetInt32(out quantity)))
        {
            return (null, "quantity must be a whole number.");
        }

        AddSource? source = Text("source") switch
        {
            "photo" => AddSource.Photo,
            "pick" => AddSource.Pick,
            "typed" => AddSource.Typed,
            null => defaultSource ?? (productId is null ? AddSource.Typed : AddSource.Pick),
            _ => null,
        };
        return source is null
            ? (null, "source must be photo, pick or typed.")
            : (new AddItem(productId, Text("name"), Text("brand"), Text("size"), quantity, source.Value), null);
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(RecogniseProductPhotoResult result) => result switch
    {
        RecogniseProductPhotoResult.Recognised r => (HttpStatusCode.OK, new
        {
            readable = r.Readable,
            items = r.Items.Select(i => new
            {
                name = i.Item.Name,
                brand = i.Item.Brand,
                size = i.Item.Size,
                kind = i.Item.Kind switch
                {
                    ItemKind.Loose => "loose",
                    ItemKind.StoreLabelled => "storeLabelled",
                    _ => "packaged",
                },
                quantity = i.Item.Quantity,
                match = i.MatchProductId is { } id ? new { productId = id, displayName = i.MatchDisplayName } : null,
            }),
        }),
        RecogniseProductPhotoResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        RecogniseProductPhotoResult.Unavailable => (HttpStatusCode.ServiceUnavailable,
            new { error = "Photo recognition isn't available right now. Pick or type the item instead." }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(AddToStockResult result) => result switch
    {
        AddToStockResult.Added a => (HttpStatusCode.OK, new
        {
            units = a.Units,
            items = a.Items.Select(i => new
            {
                productId = i.ProductId,
                displayName = i.DisplayName,
                quantity = i.Quantity,
                createdProduct = i.CreatedProduct,
            }),
        }),
        AddToStockResult.Invalid i => (HttpStatusCode.BadRequest, new { error = "Check the items.", errors = i.Errors }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(AssignStoreLabelResult result) => result switch
    {
        AssignStoreLabelResult.Assigned a => (HttpStatusCode.OK,
            new { scanId = a.ScanId, unitId = a.UnitId, productId = a.ProductId, displayName = a.DisplayName }),
        AssignStoreLabelResult.Invalid i => (HttpStatusCode.BadRequest, new { error = "Check the details.", errors = i.Errors }),
        AssignStoreLabelResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such store-label check-in." }),
        AssignStoreLabelResult.AlreadyAssigned a => (HttpStatusCode.Conflict,
            new { error = "That check-in has already been sorted out.", productId = a.ProductId }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    /// <summary>The body, or null when it's over <paramref name="max"/> bytes.</summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadAsync(HttpRequestData request, int max, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string? Timestamp(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData request, HttpStatusCode status, object body)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, WebJson));
        return response;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}
