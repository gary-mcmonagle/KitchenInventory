using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Push;

/// <summary>What a send did: subscriptions targeted, delivered, removed as gone, and failed.</summary>
public sealed record PushSendSummary(int Targeted, int Delivered, int Removed, int Failed);

/// <summary>
/// Sends a push to an audience (#77): everyone opted in to a type, named people, everyone except some, or everyone
/// except people who saw an event. Delivered → <c>lastSuccessAt</c>; gone (404/410) → the subscription is deleted;
/// failures are logged (refusals at Error: they're bugs or configuration). Never throws: a push failure must never
/// fail whatever triggered it.
/// </summary>
public sealed partial class SendPush(
    IPushSubscriptionRepository subscriptions, IPushSeenRepository seen, IPushSender sender, TimeProvider clock,
    ILogger<SendPush> logger)
{
    public async Task<PushSendSummary> ExecuteAsync(PushMessage message, PushAudience audience, CancellationToken cancellationToken)
    {
        if (PushMessage.Problem(message) is { } problem)
        {
            LogBadMessage(problem);
            return new PushSendSummary(0, 0, 0, 0);
        }

        IReadOnlyList<PushSubscription> targets;
        try
        {
            targets = await TargetsAsync(audience, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogTargetingFailed(e);
            return new PushSendSummary(0, 0, 0, 0);
        }

        int delivered = 0, removed = 0, failed = 0;
        foreach (var subscription in targets)
        {
            try
            {
                switch (await sender.SendAsync(subscription, message, cancellationToken))
                {
                    case PushOutcome.Delivered:
                        delivered++;
                        await subscriptions.MarkDeliveredAsync(subscription.UserId, subscription.Id, clock.GetUtcNow(), cancellationToken);
                        break;
                    case PushOutcome.Gone:
                        removed++;
                        await subscriptions.DeleteAsync(subscription.UserId, subscription.Id, cancellationToken);
                        break;
                    case PushOutcome.Refused:
                        failed++;
                        LogRefused(message.Topic, subscription.DeviceLabel);
                        break;
                    case PushOutcome.NotConfigured:
                        LogNotConfigured();
                        return new PushSendSummary(targets.Count, delivered, removed, targets.Count - delivered - removed);
                    default:
                        failed++;
                        LogFailed(message.Topic, subscription.DeviceLabel);
                        break;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed++;
                LogSendError(message.Topic, subscription.DeviceLabel, e);
            }
        }

        LogSent(message.Topic, targets.Count, delivered, removed, failed);
        return new PushSendSummary(targets.Count, delivered, removed, failed);
    }

    private async Task<IReadOnlyList<PushSubscription>> TargetsAsync(PushAudience audience, CancellationToken cancellationToken)
    {
        var all = await subscriptions.ListAsync(cancellationToken);
        var excluded = audience.ExceptSeenEventKey is { } key ? await seen.UsersWhoSawAsync(key, cancellationToken) : null;
        return
        [
            .. all.Where(s =>
                (audience.OptedInTo is not { } type || s.Wants(type))
                && (audience.OnlyUsers is not { } only || only.Contains(s.UserId))
                && (audience.ExceptUsers is not { } except || !except.Contains(s.UserId))
                && (excluded is null || !excluded.Contains(s.UserId))),
        ];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push {Topic}: {Targeted} targeted, {Delivered} delivered, {Removed} removed as gone, {Failed} failed.")]
    private partial void LogSent(string topic, int targeted, int delivered, int removed, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Push {Topic} to a {Device} was refused by the push service: a bug or configuration problem.")]
    private partial void LogRefused(string topic, string device);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push {Topic} to a {Device} failed after retries.")]
    private partial void LogFailed(string topic, string device);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push {Topic} to a {Device} failed.")]
    private partial void LogSendError(string topic, string device, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pushes aren't configured (no VAPID keys yet): nothing sent.")]
    private partial void LogNotConfigured();

    [LoggerMessage(Level = LogLevel.Error, Message = "A push wasn't sent: {Problem}")]
    private partial void LogBadMessage(string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't work out who to push to; nothing sent.")]
    private partial void LogTargetingFailed(Exception exception);
}

/// <summary>Schedules a push for later (#77), e.g. "everyone except people who saw scan X, in a minute".</summary>
public sealed class SchedulePush(IPushJobScheduler scheduler)
{
    public async Task<Guid> ExecuteAsync(PushMessage message, PushAudience audience, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (PushMessage.Problem(message) is { } problem)
        {
            throw new ArgumentException(problem, nameof(message));
        }

        var job = new PushJob(Guid.NewGuid(), message, audience);
        await scheduler.ScheduleAsync(job, delay, cancellationToken);
        return job.JobId;
    }
}

/// <summary>Runs a scheduled push job from the queue (#77), once: a re-delivered message finds the job claimed.</summary>
public sealed partial class RunPushJob(IPushSeenRepository seen, SendPush send, TimeProvider clock, ILogger<RunPushJob> logger)
{
    public async Task<PushSendSummary?> ExecuteAsync(PushJob job, CancellationToken cancellationToken)
    {
        if (!await seen.TryClaimJobAsync(job.JobId, clock.GetUtcNow(), cancellationToken))
        {
            LogAlreadyRun(job.JobId);
            return null;
        }

        return await send.ExecuteAsync(job.Message, job.Audience, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push job {JobId} already ran; skipped.")]
    private partial void LogAlreadyRun(Guid jobId);
}

/// <summary><c>POST /api/push/seen</c> (#77): the app saw these events on a visible screen. The user comes from the
/// token. Keys are short strings both sides can derive (e.g. <c>scan:&lt;scanId&gt;</c>).</summary>
public sealed class RecordPushSeen(IPushSeenRepository seen, TimeProvider clock)
{
    public const int MaxKeys = 50;
    public const int MaxKeyLength = 100;

    public async Task<string?> ExecuteAsync(IReadOnlyList<string> eventKeys, CurrentUser user, CancellationToken cancellationToken)
    {
        if (eventKeys.Count is 0 or > MaxKeys)
        {
            return $"eventKeys must list 1 to {MaxKeys} keys.";
        }

        if (eventKeys.Any(k => string.IsNullOrWhiteSpace(k) || k.Length > MaxKeyLength || k.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#')))
        {
            return $"Each event key must be 1 to {MaxKeyLength} characters, without / \\ ? or #.";
        }

        await seen.RecordAsync(user.ObjectId, [.. eventKeys.Distinct()], clock.GetUtcNow(), cancellationToken);
        return null;
    }
}

/// <summary>From app settings: PUSH_TEST_PER_HOUR (default 5), the limit on test pushes per person.</summary>
public sealed record PushTestSettings(int PerHour = 5);

public abstract record SendTestPushResult
{
    public sealed record Sent(PushSendSummary Summary) : SendTestPushResult;

    public sealed record NoSubscriptions : SendTestPushResult;

    public sealed record TooMany : SendTestPushResult;

    public sealed record NotConfigured : SendTestPushResult;
}

/// <summary><c>POST /api/push/test</c> (#77): a "Test notification" to the caller's own devices only, a few an hour.</summary>
public sealed class SendTestPush(
    IPushSubscriptionRepository subscriptions, SendPush send, PushSettings settings, PushTestSettings limits, TimeProvider clock)
{
    public static readonly PushMessage Message = new("Test notification", "Notifications are working on this device.", "/", "test", TimeSpan.FromHours(1));

    private readonly Dictionary<Guid, Queue<DateTimeOffset>> sent = [];
    private readonly Lock gate = new();

    public async Task<SendTestPushResult> ExecuteAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured)
        {
            return new SendTestPushResult.NotConfigured();
        }

        if ((await subscriptions.ListAsync(cancellationToken)).All(s => s.UserId != user.ObjectId))
        {
            return new SendTestPushResult.NoSubscriptions();
        }

        if (!TryTake(user.ObjectId))
        {
            return new SendTestPushResult.TooMany();
        }

        var summary = await send.ExecuteAsync(Message, new PushAudience { OnlyUsers = [user.ObjectId] }, cancellationToken);
        return new SendTestPushResult.Sent(summary);
    }

    private bool TryTake(Guid userId)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (!sent.TryGetValue(userId, out var times))
            {
                sent[userId] = times = new Queue<DateTimeOffset>();
            }

            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromHours(1))
            {
                times.Dequeue();
            }

            if (times.Count >= limits.PerHour)
            {
                return false;
            }

            times.Enqueue(now);
            return true;
        }
    }
}
