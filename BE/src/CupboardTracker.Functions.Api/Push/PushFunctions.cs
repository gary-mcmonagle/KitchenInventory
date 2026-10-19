using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Push;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Infrastructure.Push;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Functions.Api.Push;

/// <summary>Push notification plumbing (#77): config, this device's subscription and preferences, seen receipts and a
/// test push. All need a signed-in user, and only ever touch the caller's own subscriptions. See docs/api.md.</summary>
public sealed class PushFunctions(
    PushSettings settings, RegisterPushSubscription register, RemovePushSubscription remove, SetPushPreferences preferences,
    RecordPushSeen recordSeen, SendTestPush test)
{
    private const int MaxBodyBytes = 8 * 1024;

    /// <summary><c>GET /api/push/config</c> → <c>{ publicKey }</c> (null until the VAPID keys are set).</summary>
    [Function("pushConfig")]
    public Task<HttpResponseData> Config(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "push/config")] HttpRequestData request) =>
        JsonAsync(request, HttpStatusCode.OK, new { publicKey = settings.PublicKey });

    /// <summary><c>PUT /api/push/subscriptions</c> with <c>{ endpoint, keys: { p256dh, auth } }</c>: saves or refreshes
    /// this device's subscription for the caller.</summary>
    [Function("putPushSubscription")]
    public async Task<HttpResponseData> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "push/subscriptions")] HttpRequestData request,
        FunctionContext context, CancellationToken cancellationToken)
    {
        if (await ReadAsync(request, cancellationToken) is not { } body)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Body must be a JSON object." });
        }

        var input = new PushSubscriptionInput(Text(body, "endpoint"), Text(body, "keys", "p256dh"), Text(body, "keys", "auth"), Text(body, "deviceLabel"));
        var userAgent = request.Headers.TryGetValues("User-Agent", out var values) ? values.FirstOrDefault() : null;
        return await RespondAsync(request, await register.ExecuteAsync(input, userAgent, context.GetCurrentUser(), cancellationToken));
    }

    /// <summary><c>DELETE /api/push/subscriptions</c> with <c>{ endpoint }</c>: removes one of the caller's own.</summary>
    [Function("deletePushSubscription")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "push/subscriptions")] HttpRequestData request,
        FunctionContext context, CancellationToken cancellationToken)
    {
        var body = await ReadAsync(request, cancellationToken);
        return await RespondAsync(request, await remove.ExecuteAsync(body is { } b ? Text(b, "endpoint") : null, context.GetCurrentUser(), cancellationToken));
    }

    /// <summary><c>PATCH /api/push/subscriptions/preferences</c> with <c>{ endpoint, preferences: { type: bool } }</c>.</summary>
    [Function("patchPushPreferences")]
    public async Task<HttpResponseData> Preferences(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "push/subscriptions/preferences")] HttpRequestData request,
        FunctionContext context, CancellationToken cancellationToken)
    {
        var body = await ReadAsync(request, cancellationToken);
        if (body is null || body.Value.TryGetProperty("preferences", out var prefs) is false || prefs.ValueKind != JsonValueKind.Object
            || prefs.EnumerateObject().Any(p => p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Body must be { endpoint, preferences: { type: true | false } }." });
        }

        var map = prefs.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetBoolean());
        return await RespondAsync(request, await preferences.ExecuteAsync(Text(body.Value, "endpoint"), map, context.GetCurrentUser(), cancellationToken));
    }

    /// <summary><c>POST /api/push/seen</c> with <c>{ eventKeys: [string] }</c>: the caller saw these events. 204.</summary>
    [Function("pushSeen")]
    public async Task<HttpResponseData> Seen(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "push/seen")] HttpRequestData request,
        FunctionContext context, CancellationToken cancellationToken)
    {
        var body = await ReadAsync(request, cancellationToken);
        if (body is null || !body.Value.TryGetProperty("eventKeys", out var keys) || keys.ValueKind != JsonValueKind.Array
            || keys.EnumerateArray().Any(k => k.ValueKind != JsonValueKind.String))
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Body must be { eventKeys: [string] }." });
        }

        var error = await recordSeen.ExecuteAsync([.. keys.EnumerateArray().Select(k => k.GetString()!)], context.GetCurrentUser(), cancellationToken);
        return error is null ? request.CreateResponse(HttpStatusCode.NoContent) : await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
    }

    /// <summary><c>POST /api/push/test</c>: a test notification to the caller's own devices (a few an hour).</summary>
    [Function("pushTest")]
    public async Task<HttpResponseData> Test(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "push/test")] HttpRequestData request,
        FunctionContext context, CancellationToken cancellationToken) =>
        await (await test.ExecuteAsync(context.GetCurrentUser(), cancellationToken) switch
        {
            SendTestPushResult.Sent s => JsonAsync(request, HttpStatusCode.OK, new
            {
                targeted = s.Summary.Targeted,
                delivered = s.Summary.Delivered,
                removed = s.Summary.Removed,
                failed = s.Summary.Failed,
            }),
            SendTestPushResult.NoSubscriptions => JsonAsync(request, HttpStatusCode.Conflict, new { error = "This account has no devices with notifications turned on." }),
            SendTestPushResult.TooMany => JsonAsync(request, (HttpStatusCode)429, new { error = "That's enough test notifications for an hour." }),
            _ => JsonAsync(request, HttpStatusCode.ServiceUnavailable, new { error = "Notifications aren't set up yet." }),
        });

    internal static (HttpStatusCode Status, object Body) ToResponse(PushSubscriptionResult result) => result switch
    {
        PushSubscriptionResult.Saved s => (HttpStatusCode.OK, new
        {
            id = s.Subscription.Id,
            deviceLabel = s.Subscription.DeviceLabel,
            preferences = s.Subscription.Preferences,
        }),
        PushSubscriptionResult.Removed => (HttpStatusCode.OK, new { removed = true }),
        PushSubscriptionResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        PushSubscriptionResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such subscription for this account." }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static string? Text(JsonElement body, params string[] path)
    {
        var e = body;
        foreach (var name in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e))
            {
                return null;
            }
        }

        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static async Task<JsonElement?> ReadAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length is 0 or > MaxBodyBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task<HttpResponseData> RespondAsync(HttpRequestData request, PushSubscriptionResult result)
    {
        var (status, body) = ToResponse(result);
        return JsonAsync(request, status, body);
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

/// <summary>Runs delayed push jobs from the <c>push-jobs</c> queue (#77). Identity-based connection (the host's own
/// storage account). A job that can't be read is logged and dropped; a job that already ran is skipped.</summary>
public sealed partial class PushJobFunction(RunPushJob run, ILogger<PushJobFunction> logger)
{
    [Function("pushJob")]
    public async Task Run(
        [QueueTrigger(StorageQueuePushJobScheduler.QueueName, Connection = "AzureWebJobsStorage")] string message,
        CancellationToken cancellationToken)
    {
        PushJob? job;
        try
        {
            job = StorageQueuePushJobScheduler.Deserialize(message);
        }
        catch (JsonException)
        {
            job = null;
        }

        if (job is null)
        {
            LogUnreadable();
            return;
        }

        await run.ExecuteAsync(job, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A push job message couldn't be read; dropped.")]
    private partial void LogUnreadable();
}

/// <summary>Deletes push subscriptions not refreshed for 30 days (#77), daily at 03:15 UTC.</summary>
public sealed class PushExpiryTimer(ExpireStalePushSubscriptions expire)
{
    [Function("expirePushSubscriptions")]
    public Task Run([TimerTrigger("0 15 3 * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        expire.ExecuteAsync(cancellationToken);
}

/// <summary>Sends a stock change batch when it's due (#80). Never throws for a bad message: it's logged and dropped.</summary>
public sealed partial class StockPushFunction(SendStockChangePush send, ILogger<StockPushFunction> logger)
{
    [Function("stockPush")]
    public async Task Run(
        [QueueTrigger(StorageQueueStockPushScheduler.QueueName, Connection = "AzureWebJobsStorage")] string message,
        CancellationToken cancellationToken)
    {
        Guid? batchId;
        try
        {
            batchId = StorageQueueStockPushScheduler.Deserialize(message);
        }
        catch (JsonException)
        {
            batchId = null;
        }

        if (batchId is not { } id)
        {
            LogUnreadable();
            return;
        }

        await send.ExecuteAsync(id, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A stock push message couldn't be read; dropped.")]
    private partial void LogUnreadable();
}
