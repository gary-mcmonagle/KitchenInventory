using System.Globalization;
using System.Net;
using System.Text.Json;
using CupboardTracker.Application;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Infrastructure.SignalR;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.ScanFeed;

/// <summary>
/// The live scan feed (#54): negotiate a SignalR connection, catch up, and dismiss an amber toast. All need a
/// signed-in user (the bearer-token middleware answers 401 or 403 first). See docs/api.md.
/// </summary>
public sealed class ScanFeedFunctions(SignalRScanFeed signalR, GetScanFeed getScanFeed, DismissFeedItem dismiss)
{
    /// <summary><c>POST /api/negotiate</c>: called by <c>@microsoft/signalr</c> with the user's bearer token; returns
    /// where to connect and a SignalR access token for that user (<c>UserId</c> = their object ID).</summary>
    [Function("negotiate")]
    public async Task<HttpResponseData> Negotiate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "negotiate")] HttpRequestData request,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var connection = await signalR.NegotiateAsync(context.GetCurrentUser().ObjectId, cancellationToken);
        return await JsonAsync(request, HttpStatusCode.OK, new { url = connection.Url, accessToken = connection.AccessToken });
    }

    /// <summary><c>GET /api/scan-feed?since=&lt;ISO time&gt;</c>: what the app missed, and toasts still waiting for a name.</summary>
    [Function("scanFeed")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scan-feed")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var sinceText = request.Query["since"];
        DateTimeOffset? since = null;
        if (!string.IsNullOrEmpty(sinceText))
        {
            if (!Timestamps.TryParseWithOffset(sinceText, out var parsed))
            {
                return await JsonAsync(request, HttpStatusCode.BadRequest,
                    new { error = "since must be an ISO 8601 time with a timezone (e.g. 2026-10-18T07:00:00.000Z)." });
            }

            since = parsed;
        }

        var view = await getScanFeed.ExecuteAsync(since, cancellationToken);
        var response = await JsonAsync(request, HttpStatusCode.OK, ToJson(view));
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    /// <summary><c>POST /api/scan-feed/{productId}/dismiss</c>: hide that product's amber toast for the household.</summary>
    [Function("dismissFeedItem")]
    public async Task<HttpResponseData> Dismiss(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "scan-feed/{productId:guid}/dismiss")] HttpRequestData request,
        Guid productId,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var (status, body) = ToResponse(await dismiss.ExecuteAsync(productId, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    internal static object ToJson(ScanFeedView view) => new
    {
        generatedAt = view.GeneratedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        events = view.Events.Select(ScanFeedWire.WithType),
    };

    internal static (HttpStatusCode Status, object Body) ToResponse(DismissFeedItemResult result) => result switch
    {
        DismissFeedItemResult.Dismissed d => (HttpStatusCode.OK, new
        {
            productId = d.ProductId,
            feedDismissedAt = d.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        }),
        DismissFeedItemResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such product." }),
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
