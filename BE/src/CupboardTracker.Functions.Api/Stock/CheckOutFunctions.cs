using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Stock;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>Check out from the stock list, and undo it (#50); check out or re-date one unit from item detail (#53).
/// All need a signed-in user. See docs/api.md.</summary>
public sealed class CheckOutFunctions(
    CheckOutFromApp checkOut, UndoCheckOut undo, CheckOutUnit checkOutUnit, SetUnitExpiry setUnitExpiry,
    CheckOutMany checkOutMany)
{
    public const int MaxBodyBytes = 4 * 1024;

    /// <summary><c>POST /api/products/{productId}/check-out</c>: one unit, chosen like a check-out scan.</summary>
    [Function("checkOutProduct")]
    public async Task<HttpResponseData> CheckOut(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "products/{productId:guid}/check-out")] HttpRequestData request,
        Guid productId,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var (status, body) = ToResponse(await checkOut.ExecuteAsync(productId, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>POST /api/units/{unitId}/undo-check-out</c>: back in stock. Idempotent.</summary>
    [Function("undoCheckOut")]
    public async Task<HttpResponseData> Undo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{unitId:guid}/undo-check-out")] HttpRequestData request,
        Guid unitId,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var (status, body) = ToResponse(await undo.ExecuteAsync(unitId, cancellationToken, context.GetCurrentUser()));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>POST /api/units/{unitId}/check-out</c>: that unit (item detail).</summary>
    [Function("checkOutUnit")]
    public async Task<HttpResponseData> CheckOutOne(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{unitId:guid}/check-out")] HttpRequestData request,
        Guid unitId,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var (status, body) = ToResponse(await checkOutUnit.ExecuteAsync(unitId, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>POST /api/units/check-out</c> with <c>{ unitIds: [...] }</c>: clear out, many units at once (#132).</summary>
    [Function("checkOutUnits")]
    public async Task<HttpResponseData> CheckOutMany(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/check-out")] HttpRequestData request,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (unitIds, error) = ParseUnitIds(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (error is not null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (status, body) = ToResponse(await checkOutMany.ExecuteAsync(unitIds, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>PUT /api/units/{unitId}/estimated-expiry</c> with <c>{ date: "YYYY-MM-DD" }</c>: that unit only.</summary>
    [Function("setUnitExpiry")]
    public async Task<HttpResponseData> SetExpiry(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "units/{unitId:guid}/estimated-expiry")] HttpRequestData request,
        Guid unitId,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (date, error) = ParseDate(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (error is not null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (status, body) = ToResponse(await setUnitExpiry.ExecuteAsync(unitId, date, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    public const int MaxClearOutBodyBytes = 32 * 1024;

    internal static (IReadOnlyList<Guid>? UnitIds, string? Error) ParseUnitIds(ReadOnlySpan<byte> body)
    {
        const string shape = "Body must be { \"unitIds\": [\"<unit id>\", ...] }.";
        if (body.Length > MaxClearOutBodyBytes)
        {
            return (null, "Body is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("unitIds", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return (null, shape);
            }

            var ids = new List<Guid>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
                {
                    return (null, shape);
                }

                ids.Add(id);
            }

            return (ids, null);
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(CheckOutManyResult result) => result switch
    {
        CheckOutManyResult.Done d => (HttpStatusCode.OK, new
        {
            checkedOut = d.CheckedOut,
            unchanged = d.Unchanged,
            failed = d.Failed,
            units = d.Units.Select(u => new
            {
                unitId = u.UnitId,
                outcome = u.Outcome switch
                {
                    ClearOutOutcome.CheckedOut => "checkedOut",
                    ClearOutOutcome.Unchanged => "unchanged",
                    _ => "failed",
                },
                productId = u.ProductId,
            }),
        }),
        CheckOutManyResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static (string? Date, string? Error) ParseDate(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
        {
            return (null, "Body is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("date", out var value) && value.ValueKind == JsonValueKind.String
                ? (value.GetString(), null)
                : (null, "Body must be { \"date\": \"YYYY-MM-DD\" }.");
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(CheckOutUnitResult result) => result switch
    {
        CheckOutUnitResult.CheckedOut c => (HttpStatusCode.OK,
            new { unitId = c.UnitId, productId = c.ProductId, displayName = c.DisplayName, remainingCount = c.RemainingCount }),
        CheckOutUnitResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such unit." }),
        CheckOutUnitResult.NotInStock => (HttpStatusCode.Conflict, new { error = "That unit isn't in stock." }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(SetUnitExpiryResult result) => result switch
    {
        SetUnitExpiryResult.Saved s => (HttpStatusCode.OK, new
        {
            unitId = s.UnitId,
            productId = s.ProductId,
            estimatedExpiryDate = s.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            expirySource = "manual",
        }),
        SetUnitExpiryResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        SetUnitExpiryResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such unit." }),
        SetUnitExpiryResult.Conflict c => (HttpStatusCode.Conflict, new { error = c.Reason }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(CheckOutFromAppResult result) => result switch
    {
        CheckOutFromAppResult.CheckedOut c => (HttpStatusCode.OK,
            new { unitId = c.UnitId, productId = c.ProductId, displayName = c.DisplayName, remainingCount = c.RemainingCount }),
        CheckOutFromAppResult.NothingInStock => (HttpStatusCode.Conflict, new { error = "Nothing of that product is in stock." }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(UndoCheckOutResult result) => result switch
    {
        UndoCheckOutResult.InStock r => (HttpStatusCode.OK,
            new { unitId = r.UnitId, productId = r.ProductId, count = r.Count, restored = r.WasRestored }),
        UndoCheckOutResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such unit." }),
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
