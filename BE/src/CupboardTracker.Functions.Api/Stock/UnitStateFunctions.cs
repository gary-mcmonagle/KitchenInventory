using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Products;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>
/// Opened, Froze it and Defrosted on a unit, Undo, and "once opened" by hand (#65). All need a signed-in user. See
/// docs/api.md.
/// </summary>
public sealed class UnitStateFunctions(ChangeUnitState change, UndoUnitStateChange undo, SetOpenedShelfLife setOpened)
{
    /// <summary><c>POST /api/units/{unitId}/opened</c> with an optional <c>{ date }</c> (default today, UK).</summary>
    [Function("markUnitOpened")]
    public Task<HttpResponseData> Opened(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{unitId:guid}/opened")] HttpRequestData request,
        Guid unitId, FunctionContext context, CancellationToken cancellationToken) =>
        ChangeAsync(request, unitId, UnitState.Opened, context, cancellationToken);

    /// <summary><c>POST /api/units/{unitId}/frozen</c> with an optional <c>{ date }</c>.</summary>
    [Function("markUnitFrozen")]
    public Task<HttpResponseData> Frozen(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{unitId:guid}/frozen")] HttpRequestData request,
        Guid unitId, FunctionContext context, CancellationToken cancellationToken) =>
        ChangeAsync(request, unitId, UnitState.Frozen, context, cancellationToken);

    /// <summary><c>POST /api/units/{unitId}/defrosted</c> with an optional <c>{ date }</c>: frozen units only.</summary>
    [Function("markUnitDefrosted")]
    public Task<HttpResponseData> Defrosted(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{unitId:guid}/defrosted")] HttpRequestData request,
        Guid unitId, FunctionContext context, CancellationToken cancellationToken) =>
        ChangeAsync(request, unitId, UnitState.Defrosted, context, cancellationToken);

    /// <summary><c>POST /api/units/{unitId}/undo-state-change</c>: the estimate before the last state change.</summary>
    [Function("undoUnitStateChange")]
    public async Task<HttpResponseData> Undo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{unitId:guid}/undo-state-change")] HttpRequestData request,
        Guid unitId, FunctionContext context, CancellationToken cancellationToken)
    {
        var (status, body) = ToResponse(await undo.ExecuteAsync(unitId, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>PUT /api/products/{productId}/opened-shelf-life</c> with <c>{ days }</c>.</summary>
    [Function("setOpenedShelfLife")]
    public async Task<HttpResponseData> OpenedShelfLife(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "products/{productId:guid}/opened-shelf-life")] HttpRequestData request,
        Guid productId, FunctionContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (days, error) = ProductFunctions.ParseDays(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (error is not null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (status, body) = ProductFunctions.ToResponse(await setOpened.ExecuteAsync(productId, days, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    private async Task<HttpResponseData> ChangeAsync(
        HttpRequestData request, Guid unitId, UnitState state, FunctionContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (date, error) = ParseOptionalDate(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (error is not null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (status, body) = ToResponse(await change.ExecuteAsync(unitId, state, date, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary>No body, <c>{}</c>, or <c>{ "date": "YYYY-MM-DD" }</c>.</summary>
    internal static (string? Date, string? Error) ParseOptionalDate(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty || body.Trim((byte)' ').IsEmpty)
        {
            return (null, null);
        }

        if (body.Length > ProductFunctions.MaxBodyBytes)
        {
            return (null, "Body is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "Body must be a JSON object.");
            }

            if (!root.TryGetProperty("date", out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return (null, null);
            }

            return value.ValueKind == JsonValueKind.String ? (value.GetString(), null) : (null, "date must be \"YYYY-MM-DD\".");
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(ChangeUnitStateResult result) => result switch
    {
        ChangeUnitStateResult.Changed c => (HttpStatusCode.OK, new { productId = c.ProductId, unit = ProductFunctions.UnitJson(c.Unit) }),
        ChangeUnitStateResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        ChangeUnitStateResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such unit." }),
        ChangeUnitStateResult.Conflict c => (HttpStatusCode.Conflict, new { error = c.Reason }),
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
