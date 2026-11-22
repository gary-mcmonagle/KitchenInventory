using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Stock;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>Scanning with the phone camera (#133): a barcode in or out, and undo of a check-in. Both need a signed-in user.
/// See docs/api.md.</summary>
public sealed class CameraScanFunctions(CameraScan cameraScan, UndoCheckIn undoCheckIn)
{
    public const int MaxBodyBytes = 4 * 1024;

    /// <summary><c>POST /api/camera-scans</c> with <c>{ requestId, barcode, role }</c>.</summary>
    [Function("cameraScan")]
    public async Task<HttpResponseData> Scan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "camera-scans")] HttpRequestData request,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (parsed, error) = Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (error is not null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (requestId, barcode, role) = parsed;
        var (status, body) = ToResponse(await cameraScan.ExecuteAsync(requestId, barcode, role, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>POST /api/units/{unitId}/undo-check-in</c>: takes back a camera check-in.</summary>
    [Function("undoCheckIn")]
    public async Task<HttpResponseData> Undo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{unitId:guid}/undo-check-in")] HttpRequestData request,
        Guid unitId,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var (status, body) = ToResponse(await undoCheckIn.ExecuteAsync(unitId, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    internal static ((Guid RequestId, string? Barcode, string? Role) Value, string? Error) Parse(ReadOnlySpan<byte> body)
    {
        const string shape = "Body must be { \"requestId\": \"<guid>\", \"barcode\": \"<digits>\", \"role\": \"in\" | \"out\" }.";
        if (body.Length > MaxBodyBytes)
        {
            return (default, "Body is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !Text(root, "requestId", out var id) || !Guid.TryParse(id, out var requestId)
                || !Text(root, "barcode", out var barcode) || !Text(root, "role", out var role))
            {
                return (default, shape);
            }

            return ((requestId, barcode, role), null);
        }
        catch (JsonException)
        {
            return (default, "Body must be a JSON object.");
        }

        static bool Text(JsonElement root, string name, out string? value)
        {
            value = root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            return value is not null;
        }
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(CameraScanResult result) => result switch
    {
        CameraScanResult.CheckedIn c => (HttpStatusCode.OK,
            new { outcome = "checkedIn", unitId = c.UnitId, productId = c.ProductId, displayName = c.DisplayName, count = c.Count }),
        CameraScanResult.NeedsName n => (HttpStatusCode.OK,
            new { outcome = "needsName", unitId = n.UnitId, productId = n.ProductId, barcode = n.Barcode }),
        CameraScanResult.CheckedOut o => (HttpStatusCode.OK,
            new { outcome = "checkedOut", unitId = o.UnitId, productId = o.ProductId, displayName = o.DisplayName, remainingCount = o.RemainingCount }),
        CameraScanResult.NotInStock s => (HttpStatusCode.OK, new { outcome = "notInStock", barcode = s.Barcode }),
        CameraScanResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(UndoCheckInResult result) => result switch
    {
        UndoCheckInResult.Undone u => (HttpStatusCode.OK, new { unitId = u.UnitId, productId = u.ProductId, undone = u.WasUndone }),
        UndoCheckInResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such unit." }),
        UndoCheckInResult.NotUndoable n => (HttpStatusCode.Conflict, new { error = n.Reason }),
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
