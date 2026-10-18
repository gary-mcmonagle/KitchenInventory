using System.Globalization;
using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Products;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>A product, naming an unknown one (#51), and editing a known one in item detail (#53). All need a signed-in
/// user. See docs/api.md.</summary>
public sealed class ProductFunctions(
    GetProduct getProduct, NameProduct nameProduct, EditProductDetails editDetails, SetShelfLife setShelfLife)
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

    /// <summary><c>PUT /api/products/{productId}/details</c> with <c>{ name, brand?, size? }</c>: known products only.</summary>
    [Function("editProductDetails")]
    public async Task<HttpResponseData> EditDetails(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "products/{productId:guid}/details")] HttpRequestData request,
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

        var (status, body) = ToResponse(await editDetails.ExecuteAsync(productId, command, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>PUT /api/products/{productId}/shelf-life</c> with <c>{ days }</c>: for units bought from now on.</summary>
    [Function("setShelfLife")]
    public async Task<HttpResponseData> ShelfLife(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "products/{productId:guid}/shelf-life")] HttpRequestData request,
        Guid productId,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (days, error) = ParseDays(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (error is not null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (status, body) = ToResponse(await setShelfLife.ExecuteAsync(productId, days, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    internal static (int? Days, string? Error) ParseDays(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
        {
            return (null, "Body is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("days", out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var days)
                ? (days, null)
                : (null, "Body must be { \"days\": <whole number> }.");
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
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
        shelfLifeDays = view.ShelfLifeDays,
        shelfLifeSource = view.ShelfLifeSource,
        usualStorage = StorageName(view.UsualStorage),
        // How long it keeps once opened, in the freezer, and once defrosted (#65); null until estimated.
        opened = StateJson(view.Opened),
        frozen = StateJson(view.Frozen),
        defrosted = StateJson(view.Defrosted),
        openedCount = view.OpenedCount,
        frozenCount = view.FrozenCount,
        // Aisle and icon (#71, #72): the aisle ID (null until Jev has answered) and the icon name (null: use the aisle's).
        aisleId = view.Aisle?.Id,
        aisleSource = view.Aisle?.Source,
        icon = view.Icon?.Name,
        iconSource = view.Icon?.Source,
        units = view.InStockUnits.Select(UnitJson),
    };

    internal static object UnitJson(ProductUnitView u) => new
    {
        unitId = u.UnitId,
        checkedInAt = u.CheckedInAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        estimatedExpiryDate = Date(u.EstimatedExpiryDate),
        expirySource = u.ExpirySource switch
        {
            ExpirySource.Manual => "manual",
            ExpirySource.ShelfLife => "shelfLife",
            ExpirySource.Opened => "opened",
            ExpirySource.Frozen => "frozen",
            ExpirySource.Defrosted => "defrosted",
            _ => null,
        },
        daysUntilExpiry = u.DaysUntilExpiry,
        openedOn = Date(u.OpenedOn),
        frozenOn = Date(u.FrozenOn),
        defrostedOn = Date(u.DefrostedOn),
        currentStorage = StorageName(u.CurrentStorage),
        estimatePending = u.EstimatePending,
        canUndoStateChange = u.CanUndoStateChange,
    };

    private static object? StateJson(StateShelfLife? state) =>
        state is null ? null : new { days = state.Days, storage = StorageName(state.Storage), source = state.Source };

    private static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? StorageName(Storage? storage) => storage switch
    {
        Storage.Cupboard => "cupboard",
        Storage.Fridge => "fridge",
        Storage.Freezer => "freezer",
        _ => null,
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(EditProductDetailsResult result) => result switch
    {
        EditProductDetailsResult.Saved s => (HttpStatusCode.OK,
            new { productId = s.ProductId, displayName = s.DisplayName, brand = s.Brand, size = s.Size }),
        EditProductDetailsResult.Invalid i => (HttpStatusCode.BadRequest, new { error = "Check the details.", errors = i.Errors }),
        EditProductDetailsResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such product." }),
        EditProductDetailsResult.Conflict c => (HttpStatusCode.Conflict, new { error = c.Reason }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(SetShelfLifeResult result) => result switch
    {
        SetShelfLifeResult.Saved s => (HttpStatusCode.OK, new { productId = s.ProductId, shelfLifeDays = s.Days, shelfLifeSource = "manual" }),
        SetShelfLifeResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        SetShelfLifeResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such product." }),
        SetShelfLifeResult.Conflict c => (HttpStatusCode.Conflict, new { error = c.Reason }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
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
