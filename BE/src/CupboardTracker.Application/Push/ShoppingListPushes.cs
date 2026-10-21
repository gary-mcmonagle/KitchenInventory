using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Push;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Push;

/// <summary>From app settings (#86): LIST_PUSH_DELAY_SECONDS (default 180), LIST_PUSH_MAX_WAIT_SECONDS (default 900)
/// and LIST_PUSH_MIN_GAP_MINUTES (default 30).</summary>
public sealed record ListPushSettings(TimeSpan Delay, TimeSpan MaxWait, TimeSpan MinGap)
{
    public static readonly ListPushSettings Default = new(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30));
}

/// <summary>
/// The household's open list batch (#86): adds by people since it opened. It holds only the timing: what to say is
/// worked out per person from the list itself when it's due, so ticks and removals in between are never mentioned.
/// Due <see cref="ListPushSettings.Delay"/> after the last add, but no later than <see cref="ListPushSettings.MaxWait"/>
/// after the first.
/// </summary>
public sealed record ListPushBatch(Guid BatchId, DateTimeOffset FirstAt, DateTimeOffset LastAt, int Adds)
{
    public DateTimeOffset DueAt(ListPushSettings settings)
    {
        var afterLast = LastAt + settings.Delay;
        var cap = FirstAt + settings.MaxWait;
        return afterLast < cap ? afterLast : cap;
    }
}

/// <summary>
/// One person's list push state (#86): when they last looked at the List, when they were last sent a list push, and
/// when their pending follow-up (a send held back by the minimum gap) is due, if any.
/// </summary>
public sealed record ListViewer(Guid PersonId, DateTimeOffset? LastViewedAt, DateTimeOffset? LastPushAt, DateTimeOffset? FollowUpAt);

/// <summary>The one open list batch (in the pushSeen container, with an ETag), as <see cref="IStockChangeBatchRepository"/>.</summary>
public interface IListPushBatchRepository
{
    Task<Versioned<ListPushBatch>?> GetOpenAsync(CancellationToken cancellationToken);

    /// <summary>False if a batch is already open.</summary>
    Task<bool> TryCreateAsync(ListPushBatch batch, CancellationToken cancellationToken);

    /// <summary>False if the batch changed (or closed) since it was read.</summary>
    Task<bool> TryReplaceAsync(Versioned<ListPushBatch> current, ListPushBatch updated, CancellationToken cancellationToken);

    /// <summary>Closes the batch. False if it changed (or closed) since it was read.</summary>
    Task<bool> TryCloseAsync(Versioned<ListPushBatch> current, CancellationToken cancellationToken);
}

/// <summary>Each person's <see cref="ListViewer"/> (in the shoppingList container, with an ETag).</summary>
public interface IListViewerRepository
{
    Task<Versioned<ListViewer>?> GetAsync(Guid personId, CancellationToken cancellationToken);

    /// <summary>False if one exists already.</summary>
    Task<bool> TryCreateAsync(ListViewer viewer, CancellationToken cancellationToken);

    /// <summary>False if it changed since it was read.</summary>
    Task<bool> TryReplaceAsync(Versioned<ListViewer> current, ListViewer updated, CancellationToken cancellationToken);
}

/// <summary>Runs <see cref="SendListPush"/> later (a Storage queue message in Infrastructure).</summary>
public interface IListPushScheduler
{
    /// <summary>The batch's send, when it falls due.</summary>
    Task ScheduleBatchAsync(Guid batchId, TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>One person's follow-up, when their minimum gap ends.</summary>
    Task ScheduleFollowUpAsync(Guid personId, DateTimeOffset dueAt, TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Reads and changes a <see cref="ListViewer"/> with ETag retries, creating it on first use.</summary>
internal static class ListViewers
{
    private const int MaxRounds = 5;

    /// <summary>Applies <paramref name="change"/>; null from it means "nothing to change". Returns the saved state.</summary>
    public static async Task<ListViewer?> UpdateAsync(
        IListViewerRepository viewers, Guid personId, Func<ListViewer, ListViewer?> change, CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxRounds; round++)
        {
            var current = await viewers.GetAsync(personId, cancellationToken);
            var before = current?.Value ?? new ListViewer(personId, null, null, null);
            if (change(before) is not { } after)
            {
                return null;
            }

            if (current is null ? await viewers.TryCreateAsync(after, cancellationToken) : await viewers.TryReplaceAsync(current, after, cancellationToken))
            {
                return after;
            }
        }

        throw new InvalidOperationException($"Couldn't update the list push state for {personId} after {MaxRounds} conflicts.");
    }
}

/// <summary>
/// <c>POST /api/shopping-list/viewed</c> (#86): the person is looking at the List now (server time), so nothing on it
/// so far is new to them. Idempotent.
/// </summary>
public sealed partial class RecordListViewed(IListViewerRepository viewers, TimeProvider clock, ILogger<RecordListViewed> logger)
{
    public async Task ExecuteAsync(Guid personId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await ListViewers.UpdateAsync(viewers, personId, v => v.LastViewedAt >= now ? null : v with { LastViewedAt = now }, cancellationToken);
        LogViewed(personId);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "List push: {PersonId} viewed the list.")]
    private partial void LogViewed(Guid personId);
}

/// <summary>
/// After an item is added to the list (#86): opens or extends the household's list batch and schedules (or pushes back)
/// its send. Only adds by a person count: automatic adds (<see cref="ListItemSources.AutoRunOut"/>) and adds during
/// quiet hours don't open or extend a batch. Best-effort: a failure is a warning and never fails the add.
/// </summary>
public sealed partial class AddListItemToPush(
    IListPushBatchRepository batches, IListPushScheduler scheduler, ListPushSettings settings, StockSettings stock,
    TimeProvider clock, ILogger<AddListItemToPush> logger)
{
    private const int MaxRounds = 5;

    public static bool Counts(ShoppingListItem item) => item.Source != ListItemSources.AutoRunOut;

    public async Task ExecuteAsync(ShoppingListItem item, CancellationToken cancellationToken)
    {
        if (!Counts(item))
        {
            return;
        }

        try
        {
            await AddAsync(item, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(e, item.Id);
        }
    }

    private async Task AddAsync(ShoppingListItem item, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (QuietHours.IsQuiet(now, stock.HomeTimeZone))
        {
            LogQuiet(item.Id);
            return;
        }

        for (var round = 0; round < MaxRounds; round++)
        {
            var open = await batches.GetOpenAsync(cancellationToken);
            ListPushBatch next;
            if (open is null)
            {
                next = new ListPushBatch(Guid.NewGuid(), now, now, 1);
                if (!await batches.TryCreateAsync(next, cancellationToken))
                {
                    continue; // another add opened one first
                }
            }
            else
            {
                next = open.Value with { LastAt = now, Adds = open.Value.Adds + 1 };
                if (!await batches.TryReplaceAsync(open, next, cancellationToken))
                {
                    continue;
                }
            }

            var due = next.DueAt(settings);
            await scheduler.ScheduleBatchAsync(next.BatchId, due > now ? due - now : TimeSpan.Zero, cancellationToken);
            LogAdded(item.Id, next.BatchId, next.Adds, due);
            return;
        }

        throw new InvalidOperationException($"Couldn't add {item.Id} to the list batch after {MaxRounds} conflicts.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "List push: item {ItemId} joined batch {BatchId} ({Adds} adds), due {DueAt:O}.")]
    private partial void LogAdded(Guid itemId, Guid batchId, int adds, DateTimeOffset dueAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "List push: item {ItemId} added during quiet hours; doesn't trigger a push.")]
    private partial void LogQuiet(Guid itemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "List push: couldn't add item {ItemId} to the batch; it won't trigger a push.")]
    private partial void LogFailed(Exception exception, Guid itemId);
}

/// <summary>What one person was sent, or why not.</summary>
public enum ListPushDecision
{
    Sent,
    NothingNew,
    HeldBack,
    FollowUpPending,
}

/// <summary>What a batch's send did.</summary>
public sealed record ListPushSummary(int Adds, int People, int Pushed, int NothingNew, int HeldBack);

/// <summary>
/// Sends the list push (#86). When a batch is due it's closed (so a re-delivered or stale message finds nothing), then
/// each person opted in to <see cref="PreferenceKey"/> is considered: the open items someone else added since they last
/// viewed the List. Nothing new → nothing sent. Sent a list push less than <see cref="ListPushSettings.MinGap"/> ago →
/// held back, with one follow-up scheduled for when the gap ends, which looks again then. Nothing sent in quiet hours.
/// </summary>
public sealed partial class SendListPush(
    IListPushBatchRepository batches, IListViewerRepository viewers, IListPushScheduler scheduler, IShoppingListRepository list,
    IPushSubscriptionRepository subscriptions, SendPush send, ListPushSettings settings, StockSettings stock, TimeProvider clock,
    ILogger<SendListPush> logger)
{
    public const string PreferenceKey = "shoppingList";

    /// <summary>A newer list push replaces the older one (4 characters: Apple accepts it, see <see cref="PushTopic"/>).</summary>
    public const string Topic = "list";

    public const string Title = "Shopping list";

    /// <summary>Queue messages can arrive a little early; a message this close to the due time runs.</summary>
    private static readonly TimeSpan Early = TimeSpan.FromSeconds(5);

    private const int MaxRounds = 5;

    public async Task<ListPushSummary?> ExecuteBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        ListPushBatch? taken = null;
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
                LogNotDue(batchId); // pushed back by a later add: its own message sends it
                return null;
            }

            if (await batches.TryCloseAsync(open, cancellationToken))
            {
                taken = open.Value;
            }
        }

        if (taken is null)
        {
            throw new InvalidOperationException($"Couldn't close list batch {batchId} after {MaxRounds} conflicts.");
        }

        if (QuietHours.IsQuiet(clock.GetUtcNow(), stock.HomeTimeZone))
        {
            LogQuiet(batchId);
            return new ListPushSummary(taken.Adds, 0, 0, 0, 0);
        }

        var people = (await subscriptions.ListAsync(cancellationToken))
            .Where(s => s.Wants(PreferenceKey)).Select(s => s.UserId).Distinct().ToList();
        var items = await OpenItemsAsync(cancellationToken);
        int pushed = 0, nothingNew = 0, heldBack = 0;
        foreach (var person in people)
        {
            switch (await DecideAsync(person, items, cancellationToken))
            {
                case ListPushDecision.Sent:
                    pushed++;
                    break;
                case ListPushDecision.NothingNew:
                    nothingNew++;
                    break;
                default:
                    heldBack++;
                    break;
            }
        }

        LogSent(batchId, taken.Adds, people.Count, pushed, nothingNew, heldBack);
        return new ListPushSummary(taken.Adds, people.Count, pushed, nothingNew, heldBack);
    }

    /// <summary>A person's follow-up: runs only if it's still the pending one (so a re-delivered message does nothing).</summary>
    public async Task<ListPushDecision?> ExecuteFollowUpAsync(Guid personId, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        var claimed = await ListViewers.UpdateAsync(viewers, personId,
            v => v.FollowUpAt is { } pending && (pending - dueAt).Duration() < TimeSpan.FromSeconds(1) ? v with { FollowUpAt = null } : null,
            cancellationToken); // stored to the millisecond
        if (claimed is null)
        {
            LogStaleFollowUp(personId);
            return null;
        }

        if (QuietHours.IsQuiet(clock.GetUtcNow(), stock.HomeTimeZone))
        {
            LogQuietFollowUp(personId);
            return null;
        }

        if (!(await subscriptions.ListAsync(cancellationToken)).Any(s => s.UserId == personId && s.Wants(PreferenceKey)))
        {
            return null; // turned off since
        }

        var decision = await DecideAsync(personId, await OpenItemsAsync(cancellationToken), cancellationToken);
        LogFollowUp(personId, decision);
        return decision;
    }

    private async Task<List<ShoppingListItem>> OpenItemsAsync(CancellationToken cancellationToken) =>
        [.. (await list.ListAsync(cancellationToken))
            .Select(v => v.Value)
            .Where(i => i is { Removed: false, Status: ListItemStatus.Open })
            .OrderBy(i => i.AddedAt)];

    private async Task<ListPushDecision> DecideAsync(Guid person, IReadOnlyList<ShoppingListItem> items, CancellationToken cancellationToken)
    {
        var viewer = (await viewers.GetAsync(person, cancellationToken))?.Value;
        var unseen = Unseen(items, person, viewer?.LastViewedAt);
        if (unseen.Count == 0)
        {
            return ListPushDecision.NothingNew;
        }

        var now = clock.GetUtcNow();
        if (viewer?.LastPushAt is { } last && last + settings.MinGap - Early > now)
        {
            var dueAt = last + settings.MinGap;
            var scheduled = await ListViewers.UpdateAsync(viewers, person,
                v => v.FollowUpAt is { } pending && pending > now - Early ? null : v with { FollowUpAt = dueAt }, cancellationToken);
            if (scheduled is null)
            {
                return ListPushDecision.FollowUpPending; // one already waits, and will look again then
            }

            await scheduler.ScheduleFollowUpAsync(person, dueAt, dueAt - now, cancellationToken);
            return ListPushDecision.HeldBack;
        }

        await ListViewers.UpdateAsync(viewers, person, v => v with { LastPushAt = now }, cancellationToken);
        await send.ExecuteAsync(Message(unseen), new PushAudience { OnlyUsers = [person], OptedInTo = PreferenceKey }, cancellationToken);
        return ListPushDecision.Sent;
    }

    /// <summary>Open items someone else added after the person last viewed the List, oldest first.</summary>
    public static List<ShoppingListItem> Unseen(IEnumerable<ShoppingListItem> openItems, Guid person, DateTimeOffset? lastViewedAt) =>
        [.. openItems.Where(i => i.AddedBy != person && (lastViewedAt is not { } seen || i.AddedAt > seen)).OrderBy(i => i.AddedAt)];

    public static PushMessage Message(IReadOnlyList<ShoppingListItem> unseen) =>
        new(Title, Body([.. unseen.Select(i => i.Name)]), "/list", Topic, TimeSpan.FromHours(6));

    /// <summary>"3 new things on the list: Milk, Bin bags and Bread"; past 3, "Milk, Bin bags, Bread and 2 more".</summary>
    public static string Body(IReadOnlyList<string> names)
    {
        var count = names.Count == 1 ? "1 new thing" : $"{names.Count} new things";
        var shown = names.Count switch
        {
            1 => names[0],
            2 or 3 => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
            _ => $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more",
        };
        return $"{count} on the list: {shown}";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "List push: batch {BatchId} ({Adds} adds), {People} people opted in, {Pushed} pushed, {NothingNew} with nothing new, {HeldBack} held back by the gap.")]
    private partial void LogSent(Guid batchId, int adds, int people, int pushed, int nothingNew, int heldBack);

    [LoggerMessage(Level = LogLevel.Information, Message = "List push: batch {BatchId} already sent; skipped.")]
    private partial void LogNothingToSend(Guid batchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "List push: batch {BatchId} isn't due yet (a later add pushed it back); skipped.")]
    private partial void LogNotDue(Guid batchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "List push: batch {BatchId} fell due in quiet hours; not sent.")]
    private partial void LogQuiet(Guid batchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "List push: follow-up for {PersonId}: {Decision}.")]
    private partial void LogFollowUp(Guid personId, ListPushDecision decision);

    [LoggerMessage(Level = LogLevel.Information, Message = "List push: follow-up for {PersonId} is no longer pending; skipped.")]
    private partial void LogStaleFollowUp(Guid personId);

    [LoggerMessage(Level = LogLevel.Information, Message = "List push: follow-up for {PersonId} fell in quiet hours; not sent.")]
    private partial void LogQuietFollowUp(Guid personId);
}
