using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Push;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Push;

public enum StockChangeKind
{
    Added,
    Removed,
}

/// <summary>
/// One stock change for the stock change push (#80): what was added or removed, keyed like its seen receipt
/// (<c>scan:&lt;scanId&gt;</c> for the scanners). Other sources (app changes, a later card) join with their own keys.
/// </summary>
public sealed record StockChange(string EventKey, StockChangeKind Kind, string Name, DateTimeOffset At);

/// <summary>Seen receipt keys for stock changes, the same ones the app reports (<c>markSeen</c>).</summary>
public static class StockChangeKeys
{
    public static string ForScan(Guid scanId) => $"scan:{scanId}";
}

/// <summary>
/// The household's open batch of stock changes (#80): sent <see cref="StockPushSettings.Delay"/> after the last change,
/// but no later than <see cref="StockPushSettings.MaxWait"/> after the first.
/// </summary>
public sealed record StockChangeBatch(Guid BatchId, DateTimeOffset FirstAt, DateTimeOffset LastAt, IReadOnlyList<StockChange> Changes)
{
    /// <summary>A big shop is still one batch; anything past this is dropped from the push (it's still in stock).</summary>
    public const int MaxChanges = 200;

    public DateTimeOffset DueAt(StockPushSettings settings)
    {
        var afterLast = LastAt + settings.Delay;
        var cap = FirstAt + settings.MaxWait;
        return afterLast < cap ? afterLast : cap;
    }
}

/// <summary>From app settings: STOCK_PUSH_DELAY_SECONDS (default 60) and STOCK_PUSH_MAX_WAIT_SECONDS (default 300).</summary>
public sealed record StockPushSettings(TimeSpan Delay, TimeSpan MaxWait)
{
    public static readonly StockPushSettings Default = new(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5));
}

/// <summary>The one open batch (in the pushSeen container, with an ETag). Only one can exist at a time.</summary>
public interface IStockChangeBatchRepository
{
    Task<Versioned<StockChangeBatch>?> GetOpenAsync(CancellationToken cancellationToken);

    /// <summary>False if a batch is already open.</summary>
    Task<bool> TryCreateAsync(StockChangeBatch batch, CancellationToken cancellationToken);

    /// <summary>False if the batch changed (or closed) since it was read.</summary>
    Task<bool> TryReplaceAsync(Versioned<StockChangeBatch> current, StockChangeBatch updated, CancellationToken cancellationToken);

    /// <summary>Closes the batch. False if it changed (or closed) since it was read.</summary>
    Task<bool> TryCloseAsync(Versioned<StockChangeBatch> current, CancellationToken cancellationToken);
}

/// <summary>Runs <see cref="SendStockChangePush"/> for a batch after a delay (a Storage queue message in Infrastructure).</summary>
public interface IStockPushScheduler
{
    Task ScheduleAsync(Guid batchId, TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Adds a stock change to the household's open batch (#80) and schedules (or pushes back) its send. Changes during
/// quiet hours are dropped: night-time changes are never pushed later. Best-effort, like the scan feed: a failure is
/// a warning and never fails the scan.
/// </summary>
public sealed partial class AddStockChangeToPush(
    IStockChangeBatchRepository batches, IStockPushScheduler scheduler, StockPushSettings settings, StockSettings stock,
    TimeProvider clock, ILogger<AddStockChangeToPush> logger)
{
    private const int MaxRounds = 5;

    public async Task ExecuteAsync(StockChange change, CancellationToken cancellationToken)
    {
        try
        {
            await AddAsync(change, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(e, change.EventKey);
        }
    }

    private async Task AddAsync(StockChange change, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (QuietHours.IsQuiet(now, stock.HomeTimeZone))
        {
            LogQuiet(change.EventKey);
            return;
        }

        for (var round = 0; round < MaxRounds; round++)
        {
            var open = await batches.GetOpenAsync(cancellationToken);
            StockChangeBatch next;
            if (open is null)
            {
                next = new StockChangeBatch(Guid.NewGuid(), now, now, [change]);
                if (!await batches.TryCreateAsync(next, cancellationToken))
                {
                    continue; // another change opened one first
                }
            }
            else
            {
                var batch = open.Value;
                if (batch.Changes.Any(c => c.EventKey == change.EventKey))
                {
                    return; // already in this batch (a replay)
                }

                if (batch.Changes.Count >= StockChangeBatch.MaxChanges)
                {
                    LogFull(batch.BatchId, change.EventKey);
                    return;
                }

                next = batch with { LastAt = now, Changes = [.. batch.Changes, change] };
                if (!await batches.TryReplaceAsync(open, next, cancellationToken))
                {
                    continue;
                }
            }

            var due = next.DueAt(settings);
            await scheduler.ScheduleAsync(next.BatchId, due > now ? due - now : TimeSpan.Zero, cancellationToken);
            LogAdded(change.EventKey, next.BatchId, next.Changes.Count, due);
            return;
        }

        throw new InvalidOperationException($"Couldn't add {change.EventKey} to the stock change batch after {MaxRounds} conflicts.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stock change push: {EventKey} joined batch {BatchId} ({Count} changes), due {DueAt:O}.")]
    private partial void LogAdded(string eventKey, Guid batchId, int count, DateTimeOffset dueAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stock change push: {EventKey} during quiet hours; not pushed.")]
    private partial void LogQuiet(string eventKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stock change push: batch {BatchId} is full; {EventKey} not pushed.")]
    private partial void LogFull(Guid batchId, string eventKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stock change push: couldn't add {EventKey} to the batch; it won't be pushed.")]
    private partial void LogFailed(Exception exception, string eventKey);
}

/// <summary>What a batch's send did.</summary>
public sealed record StockPushSummary(int Changes, int People, int Pushed, int AllSeen);

/// <summary>
/// Sends a due batch (#80): closes it (so a re-delivered or stale queue message finds nothing to send), then gives each
/// person opted in to stock changes on any device one push for the changes they have no seen receipt for, on any of
/// their devices. Nobody pushed if they saw them all. Nothing sent if the batch falls due in quiet hours.
/// </summary>
public sealed partial class SendStockChangePush(
    IStockChangeBatchRepository batches, IPushSubscriptionRepository subscriptions, IPushSeenRepository seen, SendPush send,
    StockPushSettings settings, StockSettings stock, TimeProvider clock, ILogger<SendStockChangePush> logger)
{
    public const string PreferenceKey = "stockChanges";
    public const string Topic = "stock";

    /// <summary>Queue messages can arrive a little early; a message this close to the due time sends.</summary>
    private static readonly TimeSpan Early = TimeSpan.FromSeconds(5);

    private const int MaxRounds = 5;

    public async Task<StockPushSummary?> ExecuteAsync(Guid batchId, CancellationToken cancellationToken)
    {
        StockChangeBatch? taken = null;
        for (var round = 0; round < MaxRounds && taken is null; round++)
        {
            var open = await batches.GetOpenAsync(cancellationToken);
            if (open is null || open.Value.BatchId != batchId)
            {
                LogNothingToSend(batchId);
                return null;
            }

            if (open.Value.DueAt(settings) - Early > clock.GetUtcNow())
            {
                LogNotDue(batchId); // pushed back by a later change: its own message sends it
                return null;
            }

            if (await batches.TryCloseAsync(open, cancellationToken))
            {
                taken = open.Value;
            }
        }

        if (taken is null)
        {
            throw new InvalidOperationException($"Couldn't close stock change batch {batchId} after {MaxRounds} conflicts.");
        }

        if (QuietHours.IsQuiet(clock.GetUtcNow(), stock.HomeTimeZone))
        {
            LogQuiet(batchId, taken.Changes.Count);
            return new StockPushSummary(taken.Changes.Count, 0, 0, 0);
        }

        var people = (await subscriptions.ListAsync(cancellationToken))
            .Where(s => s.Wants(PreferenceKey)).Select(s => s.UserId).Distinct().ToList();
        var seenBy = new Dictionary<string, IReadOnlySet<Guid>>();
        foreach (var key in taken.Changes.Select(c => c.EventKey).Distinct())
        {
            seenBy[key] = await seen.UsersWhoSawAsync(key, cancellationToken);
        }

        int pushed = 0, allSeen = 0;
        foreach (var person in people)
        {
            var missed = taken.Changes.Where(c => !seenBy[c.EventKey].Contains(person)).ToList();
            if (missed.Count == 0)
            {
                allSeen++;
                continue;
            }

            await send.ExecuteAsync(Message(missed), new PushAudience { OnlyUsers = [person], OptedInTo = PreferenceKey }, cancellationToken);
            pushed++;
        }

        LogSent(batchId, taken.Changes.Count, people.Count, pushed, allSeen);
        return new StockPushSummary(taken.Changes.Count, people.Count, pushed, allSeen);
    }

    public static PushMessage Message(IReadOnlyList<StockChange> changes) =>
        new("Cupboard updated", Body(changes), "/", Topic, TimeSpan.FromHours(3));

    /// <summary><c>Added Milk, Bread and 4 more · Removed Hummus</c>: up to 3 names a side, repeats as "2 × Milk",
    /// a side with nothing left out.</summary>
    public static string Body(IReadOnlyList<StockChange> changes)
    {
        var sides = new[] { (StockChangeKind.Added, "Added"), (StockChangeKind.Removed, "Removed") }
            .Select(s => (Label: s.Item2, Names: Names(changes.Where(c => c.Kind == s.Item1))))
            .Where(s => s.Names.Count > 0)
            .Select(s => $"{s.Label} {List(s.Names)}");
        return string.Join(" · ", sides);
    }

    private static List<string> Names(IEnumerable<StockChange> changes) =>
    [
        .. changes.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Count()} × {g.First().Name}" : g.First().Name),
    ];

    private static string List(List<string> names) => names.Count switch
    {
        1 => names[0],
        2 or 3 => $"{string.Join(", ", names[..^1])} and {names[^1]}",
        _ => $"{string.Join(", ", names[..3])} and {names.Count - 3} more",
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Stock change push: batch {BatchId} ({Changes} changes), {People} people opted in, {Pushed} pushed, {AllSeen} saw everything.")]
    private partial void LogSent(Guid batchId, int changes, int people, int pushed, int allSeen);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stock change push: batch {BatchId} already sent; skipped.")]
    private partial void LogNothingToSend(Guid batchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stock change push: batch {BatchId} isn't due yet (a later change pushed it back); skipped.")]
    private partial void LogNotDue(Guid batchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stock change push: batch {BatchId} ({Changes} changes) fell due in quiet hours; not sent.")]
    private partial void LogQuiet(Guid batchId, int changes);
}
