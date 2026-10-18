using System.Globalization;
using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Scanners;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Scanners;

/// <summary>The scanners screen (#55): health and what needs sorting, and dismissing an unmatched check-out. Both need
/// a signed-in user. See docs/api.md.</summary>
public sealed class ScannersFunctions(GetScanners getScanners, DismissUnmatchedCheckOut dismiss)
{
    /// <summary><c>GET /api/scanners</c></summary>
    [Function("scanners")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scanners")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var response = await JsonAsync(request, HttpStatusCode.OK, ToJson(await getScanners.ExecuteAsync(cancellationToken)));
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    /// <summary><c>POST /api/unmatched-check-outs/{scanId}/dismiss</c>: idempotent.</summary>
    [Function("dismissUnmatchedCheckOut")]
    public async Task<HttpResponseData> Dismiss(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "unmatched-check-outs/{scanId:guid}/dismiss")] HttpRequestData request,
        Guid scanId,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var (status, body) = ToResponse(await dismiss.ExecuteAsync(scanId, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    internal static object ToJson(ScannersView view) => new
    {
        generatedAt = Timestamp(view.GeneratedAt),
        hub = view.Hub is { } hub
            ? new
            {
                hubId = hub.HubId,
                lastSeenAt = Timestamp(hub.LastSeenAt),
                heartbeatIntervalSeconds = (int)hub.HeartbeatInterval.TotalSeconds,
                offlineThresholdMinutes = (int)hub.OfflineThreshold.TotalMinutes,
                online = hub.Online,
            }
            : null,
        devices = view.Devices.Select(d => new
        {
            deviceId = d.DeviceId,
            role = d.Role.ToWireName(),
            present = d.Present,
            presentSince = Timestamp(d.PresentSince),
            absentSince = Timestamp(d.AbsentSince),
            lastScanAt = Timestamp(d.LastScanAt),
        }),
        noMatchCheckIns = view.NoMatchCheckIns.Select(n => new
        {
            productId = n.ProductId,
            barcode = n.Barcode,
            status = n.Status == ProductStatus.NoMatch ? "noMatch" : "lookupFailed",
            scannedAt = Timestamp(n.ScannedAt),
            unitId = n.UnitId,
            inStockCount = n.InStockCount,
        }),
        unmatchedCheckOuts = view.UnmatchedCheckOuts.Select(u => new
        {
            scanId = u.ScanId,
            barcode = u.Barcode,
            productId = u.ProductId,
            displayName = u.DisplayName,
            scannedAt = Timestamp(u.ScannedAt),
        }),
        recentlyCheckedOut = view.RecentlyCheckedOut.Select(r => new
        {
            unitId = r.UnitId,
            productId = r.ProductId,
            displayName = r.DisplayName,
            checkedOutAt = Timestamp(r.CheckedOutAt),
            source = r.Source,
        }),
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(DismissUnmatchedCheckOutResult result) => result switch
    {
        DismissUnmatchedCheckOutResult.Dismissed d => (HttpStatusCode.OK, new { scanId = d.ScanId, dismissed = true }),
        DismissUnmatchedCheckOutResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such unmatched check-out." }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

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
