using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>"Still got this?" (#94): Still here and its Undo, Gone (a stock-check check-out; Undo is
/// <c>undo-check-out</c>), the expired units for the sheet, and the Tue/Thu/Sun push. All HTTP ones need a signed-in
/// user. See docs/api.md.</summary>
public sealed class StillGotThisFunctions(MarkStillHere stillHere, CheckOutUnit checkOutUnit, GetExpiredUnits expired)
{
    /// <summary><c>POST /api/units/{unitId}/still-here</c>: it stops asking for a couple of days. Idempotent.</summary>
    [Function("stillHere")]
    public async Task<HttpResponseData> StillHere(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{unitId:guid}/still-here")] HttpRequestData request,
        Guid unitId, FunctionContext context, CancellationToken cancellationToken)
    {
        var (status, body) = ToResponse(await stillHere.ExecuteAsync(unitId, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>POST /api/units/{unitId}/undo-still-here</c>: it asks again. Idempotent.</summary>
    [Function("undoStillHere")]
    public async Task<HttpResponseData> UndoStillHere(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{unitId:guid}/undo-still-here")] HttpRequestData request,
        Guid unitId, FunctionContext context, CancellationToken cancellationToken)
    {
        var (status, body) = ToResponse(await stillHere.UndoAsync(unitId, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>POST /api/units/{unitId}/gone</c>: checks out that unit with <c>checkOutReason: "stockCheck"</c>.</summary>
    [Function("unitGone")]
    public async Task<HttpResponseData> Gone(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{unitId:guid}/gone")] HttpRequestData request,
        Guid unitId, FunctionContext context, CancellationToken cancellationToken)
    {
        var (status, body) = CheckOutFunctions.ToResponse(
            await checkOutUnit.ExecuteAsync(unitId, context.GetCurrentUser(), cancellationToken, CheckOutReasons.StockCheck));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>GET /api/stock/expired</c>: what the "Still got these?" sheet lists.</summary>
    [Function("expiredUnits")]
    public async Task<HttpResponseData> Expired(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "stock/expired")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var units = await expired.ExecuteAsync(cancellationToken);
        var response = await JsonAsync(request, HttpStatusCode.OK, ToJson(units));
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    internal static object ToJson(IReadOnlyList<ExpiredUnit> units) => new
    {
        units = units.Select(u => new
        {
            unitId = u.UnitId,
            productId = u.ProductId,
            name = u.Name,
            storage = u.Storage switch
            {
                Storage.Fridge => "fridge",
                Storage.Freezer => "freezer",
                Storage.Cupboard => "cupboard",
                _ => null,
            },
            boughtOn = StockFunction.Date(u.BoughtOn),
            openedOn = u.OpenedOn is { } o ? StockFunction.Date(o) : null,
            estimatedExpiryDate = StockFunction.Date(u.EstimatedExpiryDate),
            daysOver = u.DaysOver,
        }),
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(StillHereResult result) => result switch
    {
        StillHereResult.Saved s => (HttpStatusCode.OK, new
        {
            unitId = s.UnitId,
            productId = s.ProductId,
            lastConfirmedAt = s.LastConfirmedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        }),
        StillHereResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such unit." }),
        StillHereResult.NotInStock => (HttpStatusCode.Conflict, new { error = "That unit isn't in stock." }),
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

/// <summary>
/// The "Still got this?" push (#94): 18:00 UK on Tue, Thu and Sun. Linux Flex Consumption has no time zone setting, so
/// this runs at 17:00 and 18:00 UTC on those days (the same days in the UK at that hour) and only the run in the 18:00
/// hour in the UK sends. A failure is retried here; the day is claimed only once.
/// </summary>
public sealed class StillGotThisPushTimer(SendStillGotThisPush send)
{
    [Function("stillGotThisPush")]
    [ExponentialBackoffRetry(3, "00:00:30", "00:05:00")]
    public Task Run([TimerTrigger("0 0 17,18 * * 0,2,4")] TimerInfo timer, CancellationToken cancellationToken) =>
        send.ExecuteAsync(cancellationToken);
}
