using System.Globalization;
using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Products;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>A product, and naming an unknown one (#51). Both need a signed-in user. See docs/api.md.</summary>
public sealed class ProductFunctions(GetProduct getProduct, NameProduct nameProduct)
{
    public const int MaxBodyBytes = 4 * 1024;

    /// <summary><c>GET /api/products/{productId}</c></summary>
    [Function("getProduct")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/{productId:guid}")] HttpRequestData request,
        Guid productId,
        CancellationToken cancellationToken)
    {
        var view = await getProduct.ExecuteAsync(productId, cancellationToken);
        return view is null
            ? await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such product." })
            : await JsonAsync(request, HttpStatusCode.OK, ToJson(view));
    }

    /// <summary><c>PUT /api/products/{productId}/name</c> with <c>{ name, brand?, size? }</c>.</summary>
    [Function("nameProduct")]
    public async Task<HttpResponseData> Name(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "products/{productId:guid}/name")] HttpRequestData request,
        Guid productId,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (command, error) = ParseName(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (command is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error, errors = new Dictionary<string, string>() });
        }

        var (status, body) = ToResponse(await nameProduct.ExecuteAsync(productId, command, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    internal static (NameProductCommand? Command, string? Error) ParseName(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
        {
            return (null, "Body is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "Body must be a JSON object.");
            }

            string? Field(string name) =>
                document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            return (new NameProductCommand(Field("name"), Field("brand"), Field("size")), null);
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    internal static object ToJson(ProductView view) => new
    {
        productId = view.ProductId,
        barcode = view.Barcode,
        status = view.Status switch
        {
            ProductStatus.Known => "known",
            ProductStatus.NoMatch => "noMatch",
            _ => "lookupFailed",
        },
        displayName = view.DisplayName,
        brand = view.Brand,
        size = view.Size,
        inStockCount = view.InStockCount,
        latestCheckedInAt = view.LatestCheckedInAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        latestDeviceRole = view.LatestDeviceRole?.ToWireName(),
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(NameProductResult result) => result switch
    {
        NameProductResult.Named n => (HttpStatusCode.OK, new { productId = n.ProductId, displayName = n.DisplayName }),
        NameProductResult.Invalid i => (HttpStatusCode.BadRequest, new { error = "Check the details.", errors = i.Errors }),
        NameProductResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such product." }),
        NameProductResult.AlreadyNamed a => (HttpStatusCode.Conflict,
            new { error = "This product already has a name.", displayName = a.DisplayName }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData request, HttpStatusCode status, object body)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, WebJson));
        return response;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}
