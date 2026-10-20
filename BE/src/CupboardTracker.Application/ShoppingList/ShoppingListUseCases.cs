using CupboardTracker.Application.Products;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.ShoppingList;

/// <summary>
/// Best-effort live updates (#84), like <see cref="ScanFeed.ScanFeedNotifier"/>: called after a write succeeds; a failure
/// is a warning and never fails the change (apps catch up with <c>since</c>).
/// </summary>
public sealed partial class ShoppingListNotifier(IShoppingListPublisher publisher, ILogger<ShoppingListNotifier> logger)
{
    public Task ChangedAsync(ShoppingListItem item, CancellationToken cancellationToken) =>
        Try(() => publisher.ItemChangedAsync(item, cancellationToken), "shoppingListItemChanged", item.Id.ToString());

    public Task RemovedAsync(ShoppingListItem item, CancellationToken cancellationToken) =>
        Try(() => publisher.ItemRemovedAsync(item.Id, item.Version, cancellationToken), "shoppingListItemRemoved", item.Id.ToString());

    public Task ClearedAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        Try(() => publisher.ClearedAsync(ids, cancellationToken), "shoppingListCleared", $"{ids.Count} items");

    private async Task Try(Func<Task> send, string name, string key)
    {
        try
        {
            await send();
            LogSent(name, key);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(e, name, key);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shopping list: sent {EventName} for {Key}.")]
    private partial void LogSent(string eventName, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopping list: couldn't send {EventName} for {Key}; apps catch up from the API.")]
    private partial void LogFailed(Exception exception, string eventName, string key);
}

/// <summary>An add from the app (or another feature with its own source and a fixed change id).</summary>
public sealed record AddListItem(Guid Id, string ChangeId, string? Name, string? Note, Guid? ProductId, string Source);

public abstract record ListChangeResult
{
    /// <summary>The item as it is now. <see cref="Merged"/>: the name was already open, so this is that item (with the
    /// note added if one was given).</summary>
    public sealed record Changed(ShoppingListItem Item, bool Merged = false) : ListChangeResult;

    public sealed record Invalid(string Error) : ListChangeResult;

    public sealed record NotFound : ListChangeResult;
}

/// <summary>
/// <c>PUT /api/shopping-list/items/{id}</c> (#84): adds to the list at once. Idempotent by id and change id. A name
/// already open isn't added twice: the open item is returned, with the note added if one was given. Items from a
/// product take its aisle; typed ones reuse a remembered answer or are queued for Jev (the app shows a spinner).
/// </summary>
public sealed partial class AddToShoppingList(
    IShoppingListRepository list, IProductRepository products, IListAisleScheduler aisles, ShoppingListNotifier notify,
    TimeProvider clock, ILogger<AddToShoppingList> logger)
{
    private const int MaxRounds = 5;

    public async Task<ListChangeResult> ExecuteAsync(AddListItem add, CurrentUser user, CancellationToken cancellationToken)
    {
        if (add.Id == Guid.Empty || string.IsNullOrWhiteSpace(add.ChangeId) || add.ChangeId.Length > 64)
        {
            return new ListChangeResult.Invalid("Send an item id and a changeId (at most 64 characters).");
        }

        if (!ListItemSources.FromTheApp.Contains(add.Source) && add.Source.Length is 0 or > 32)
        {
            return new ListChangeResult.Invalid("Unknown source.");
        }

        var note = string.IsNullOrWhiteSpace(add.Note) ? null : add.Note.Trim();
        if (note?.Length > ShoppingListNames.MaxNote)
        {
            return new ListChangeResult.Invalid($"Keep the note to {ShoppingListNames.MaxNote} characters or fewer.");
        }

        var product = add.ProductId is { } productId ? await products.GetAsync(productId, cancellationToken) : null;
        if (add.ProductId is not null && product is null)
        {
            return new ListChangeResult.Invalid("No such product.");
        }

        var name = (string.IsNullOrWhiteSpace(add.Name) ? product?.Generic?.Name ?? product?.DisplayName : add.Name)?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > ShoppingListNames.MaxName)
        {
            return new ListChangeResult.Invalid($"Enter a name of 1 to {ShoppingListNames.MaxName} characters.");
        }

        if (await list.GetAsync(add.Id, cancellationToken) is { } existing)
        {
            return new ListChangeResult.Changed(existing.Value); // a retry (or an id reused): the add already happened
        }

        var normalized = ShoppingListNames.Normalize(name);
        var now = clock.GetUtcNow();
        for (var round = 0; round < MaxRounds; round++)
        {
            var open = (await list.ListAsync(cancellationToken))
                .FirstOrDefault(i => i.Value is { Removed: false, Status: ListItemStatus.Open } v && v.NormalizedName == normalized);
            if (open is not null)
            {
                if (note is null || string.Equals(open.Value.Note, note, StringComparison.Ordinal))
                {
                    return new ListChangeResult.Changed(open.Value, Merged: true);
                }

                var merged = open.Value.Changed(add.ChangeId, now) with { Note = open.Value.Note is { } n ? $"{n}, {note}" : note };
                if (!await list.TryReplaceAsync(open, merged with { Note = Cap(merged.Note!) }, cancellationToken))
                {
                    continue;
                }

                var saved = merged with { Note = Cap(merged.Note!) };
                await notify.ChangedAsync(saved, cancellationToken);
                return new ListChangeResult.Changed(saved, Merged: true);
            }

            var (aisle, aisleSource) = product?.Aisle is { } pa
                ? (pa.Id, ListAisleSources.Product)
                : await list.GetMemoryAsync(normalized, cancellationToken) is { Aisle: { } remembered }
                    ? (remembered, ListAisleSources.Jev)
                    : ((string?)null, (string?)null);
            var item = new ShoppingListItem(add.Id, name, normalized, note, product?.ProductId, aisle, aisleSource, add.Source,
                ListItemStatus.Open, user.ObjectId, user.DisplayName, now, null, null, null, now, 1, add.ChangeId);
            if (!await list.TryCreateAsync(item, cancellationToken))
            {
                return new ListChangeResult.Changed((await list.GetAsync(add.Id, cancellationToken))!.Value); // created by a racing retry
            }

            await RememberAsync(normalized, name, aisle, now, cancellationToken);
            LogAdded(item.Id, item.Source, aisleSource ?? "pending");
            await notify.ChangedAsync(item, cancellationToken);
            if (aisle is null)
            {
                await aisles.ScheduleAsync(item.Id, cancellationToken);
            }

            return new ListChangeResult.Changed(item);
        }

        throw new InvalidOperationException($"The list kept changing; couldn't add {add.Id}.");
    }

    private async Task RememberAsync(string normalized, string name, string? aisle, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var memory = await list.GetMemoryAsync(normalized, cancellationToken);
        await list.SaveMemoryAsync(
            new ListMemory(normalized, name, aisle ?? memory?.Aisle, (memory?.TimesAdded ?? 0) + 1, now), cancellationToken);
    }

    private static string Cap(string note) => note.Length > ShoppingListNames.MaxNote ? note[..ShoppingListNames.MaxNote].TrimEnd() : note;

    [LoggerMessage(Level = LogLevel.Information, Message = "Shopping list: added {ItemId} ({Source}), aisle {AisleSource}.")]
    private partial void LogAdded(Guid itemId, string source, string aisleSource);
}

/// <summary>Tick off, put back, and remove (#84). Each is idempotent by change id: the same change sent again does nothing.</summary>
public sealed partial class ChangeShoppingListItem(
    IShoppingListRepository list, ShoppingListNotifier notify, TimeProvider clock, ILogger<ChangeShoppingListItem> logger)
{
    private const int MaxRounds = 5;

    /// <summary>Tick off. <paramref name="at"/> is when it was ticked on the phone (offline changes arrive late, #85):
    /// it applies only if newer than the item's last tick or untick. A time in the future counts as now.</summary>
    public Task<ListChangeResult> DoneAsync(Guid id, string changeId, CurrentUser user, CancellationToken cancellationToken, DateTimeOffset? at = null) =>
        ChangeAsync(id, changeId, (i, now) =>
        {
            var when = Clamp(at, now);
            return i.Status == ListItemStatus.Done || (at is not null && Stale(i, when)) ? null
                : i.Changed(changeId, now) with
                {
                    Status = ListItemStatus.Done, DoneBy = user.ObjectId, DoneByName = user.DisplayName, DoneAt = when, StatusChangedAt = when,
                };
        }, cancellationToken);

    /// <summary>Put back on the list; the same "newer wins" rule as <see cref="DoneAsync"/>.</summary>
    public Task<ListChangeResult> UndoneAsync(Guid id, string changeId, CancellationToken cancellationToken, DateTimeOffset? at = null) =>
        ChangeAsync(id, changeId, (i, now) =>
        {
            var when = Clamp(at, now);
            return i.Status == ListItemStatus.Open || (at is not null && Stale(i, when)) ? null
                : i.Changed(changeId, now) with { Status = ListItemStatus.Open, DoneBy = null, DoneByName = null, DoneAt = null, StatusChangedAt = when };
        }, cancellationToken);

    private static DateTimeOffset Clamp(DateTimeOffset? at, DateTimeOffset now) => at is { } a && a < now ? a : now;

    /// <summary>A change that says when it was made (the outbox always does) loses to a newer or equal tick or untick;
    /// one that doesn't applies in arrival order.</summary>
    private static bool Stale(ShoppingListItem item, DateTimeOffset when) => item.StatusChangedAt is { } last && when <= last; // a replay of the same change is stale too

    public Task<ListChangeResult> RemoveAsync(Guid id, string changeId, CancellationToken cancellationToken) =>
        ChangeAsync(id, changeId, (i, now) => i.Changed(changeId, now) with { Removed = true }, cancellationToken);

    private async Task<ListChangeResult> ChangeAsync(
        Guid id, string changeId, Func<ShoppingListItem, DateTimeOffset, ShoppingListItem?> change, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(changeId) || changeId.Length > 64)
        {
            return new ListChangeResult.Invalid("Send a changeId (at most 64 characters).");
        }

        for (var round = 0; round < MaxRounds; round++)
        {
            var current = await list.GetAsync(id, cancellationToken);
            if (current is null)
            {
                return new ListChangeResult.NotFound();
            }

            if (current.Value.Removed || current.Value.ChangeId == changeId)
            {
                return new ListChangeResult.Changed(current.Value); // already gone, or this change was already applied
            }

            if (change(current.Value, clock.GetUtcNow()) is not { } updated)
            {
                return new ListChangeResult.Changed(current.Value); // already in that state
            }

            if (!await list.TryReplaceAsync(current, updated, cancellationToken))
            {
                continue;
            }

            LogChanged(id, updated.Removed ? "removed" : updated.Status == ListItemStatus.Done ? "done" : "open", updated.Version);
            await (updated.Removed ? notify.RemovedAsync(updated, cancellationToken) : notify.ChangedAsync(updated, cancellationToken));
            return new ListChangeResult.Changed(updated);
        }

        throw new InvalidOperationException($"List item {id} kept changing.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shopping list: {ItemId} is now {State} (version {Version}).")]
    private partial void LogChanged(Guid itemId, string state, long version);
}

/// <summary>
/// "Clear ticked" (#84), and the timer's clean-up of items ticked off more than a day ago: both turn done items into
/// tombstones and tell open apps.
/// </summary>
public sealed partial class ClearDoneShoppingListItems(
    IShoppingListRepository list, ShoppingListNotifier notify, TimeProvider clock, ILogger<ClearDoneShoppingListItems> logger)
{
    public static readonly TimeSpan RecentlyDone = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<Guid>> ExecuteAsync(bool onlyOlderThanADay, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var cleared = new List<Guid>();
        foreach (var item in await list.ListAsync(cancellationToken))
        {
            if (item.Value is not { Removed: false, Status: ListItemStatus.Done, DoneAt: { } doneAt }
                || (onlyOlderThanADay && now - doneAt < RecentlyDone))
            {
                continue;
            }

            if (await list.TryReplaceAsync(item, item.Value.Changed($"clear:{now.ToUnixTimeMilliseconds()}", now) with { Removed = true }, cancellationToken))
            {
                cleared.Add(item.Value.Id);
            }
        }

        if (cleared.Count > 0)
        {
            LogCleared(cleared.Count, onlyOlderThanADay ? "the daily clean-up" : "Clear ticked");
            await notify.ClearedAsync(cleared, cancellationToken);
        }

        return cleared;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shopping list: cleared {Count} ticked items ({Why}).")]
    private partial void LogCleared(int count, string why);
}

/// <summary>The list as the app shows it, or what changed since <c>asOf</c> (including tombstones).</summary>
public sealed record ShoppingListView(DateTimeOffset AsOf, IReadOnlyList<ShoppingListItem> Items);

/// <summary><c>GET /api/shopping-list[?since=]</c> (#84): open items (oldest first) and ones ticked off in the last day;
/// or, with <c>since</c>, every item changed since then, removed ones as tombstones.</summary>
public sealed class GetShoppingList(IShoppingListRepository list, TimeProvider clock)
{
    /// <summary>Clocks differ a little between writes: a catch-up looks back this much further (apps keep the higher
    /// version, so a repeat is harmless).</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromSeconds(5);

    public async Task<ShoppingListView> ExecuteAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var items = (await list.ListAsync(cancellationToken)).Select(v => v.Value);
        var selected = since is { } s
            ? items.Where(i => i.UpdatedAt >= s - Overlap)
            : items.Where(i => !i.Removed && (i.Status == ListItemStatus.Open || now - i.DoneAt < ClearDoneShoppingListItems.RecentlyDone));
        return new ShoppingListView(now, [.. selected.OrderBy(i => i.AddedAt).ThenBy(i => i.Id)]);
    }
}

/// <summary>A suggestion while typing: a name, how many are in stock across every product with it, and a product to
/// take the aisle from.</summary>
public sealed record ListSuggestion(string Name, int InStock, Guid? ProductId);

/// <summary>
/// <c>GET /api/shopping-list/suggestions?q=</c> (#84): up to 8 names containing the text, from products' generic names
/// (display names when they have none) and past list items, ranked by names starting with it, generic names, how often
/// and how recently they were used. <c>InStock</c> sums units across every product with that generic name. Names
/// already open are left out.
/// </summary>
public sealed class SuggestListItems(IShoppingListRepository list, IProductRepository products, GetStock stock)
{
    public const int Max = 8;

    public async Task<IReadOnlyList<ListSuggestion>> ExecuteAsync(string? query, CancellationToken cancellationToken)
    {
        var q = ShoppingListNames.Normalize(query ?? "");
        if (q.Length == 0)
        {
            return [];
        }

        var open = (await list.ListAsync(cancellationToken))
            .Where(i => i.Value is { Removed: false, Status: ListItemStatus.Open })
            .Select(i => i.Value.NormalizedName).ToHashSet();
        var counts = (await stock.ExecuteAsync(cancellationToken)).Aisles.SelectMany(a => a.Products)
            .ToDictionary(p => p.ProductId, p => p.Count);
        var memory = (await list.ListMemoryAsync(cancellationToken)).ToDictionary(m => m.NormalizedName);

        var candidates = new Dictionary<string, Candidate>();
        foreach (var product in await products.ListKnownAsync(cancellationToken))
        {
            var name = product.Generic?.Name ?? product.DisplayName;
            var key = ShoppingListNames.Normalize(name);
            var units = counts.GetValueOrDefault(product.ProductId);
            var c = candidates.TryGetValue(key, out var found) ? found : candidates[key] = new Candidate(name, product.Generic is not null);
            c.InStock += units;
            c.Uses++;
            c.LastUsed = Latest(c.LastUsed, product.LastAddedAt);
            if (c.ProductId is null || units > 0)
            {
                c.ProductId = product.ProductId;
            }
        }

        foreach (var (key, m) in memory)
        {
            var c = candidates.TryGetValue(key, out var found) ? found : candidates[key] = new Candidate(m.Name, false);
            c.Uses += m.TimesAdded;
            c.LastUsed = Latest(c.LastUsed, m.LastAddedAt);
        }

        return
        [
            .. candidates
                .Where(kv => kv.Key.Contains(q, StringComparison.Ordinal) && !open.Contains(kv.Key))
                .OrderBy(kv => kv.Key.StartsWith(q, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(kv => kv.Value.Generic ? 0 : 1)
                .ThenByDescending(kv => kv.Value.Uses)
                .ThenByDescending(kv => kv.Value.LastUsed)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(Max)
                .Select(kv => new ListSuggestion(kv.Value.Name, kv.Value.InStock, kv.Value.ProductId)),
        ];
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private sealed class Candidate(string name, bool generic)
    {
        public string Name { get; } = name;

        public bool Generic { get; } = generic;

        public int InStock { get; set; }

        public int Uses { get; set; }

        public DateTimeOffset? LastUsed { get; set; }

        public Guid? ProductId { get; set; }
    }
}

/// <summary>
/// A typed item's aisle (#84), after the add: a remembered answer for that name, or Jev's choice over the aisles (the
/// aisles feature's question). Jev unreachable → Other for now, retried by the timer. Tells open apps when it arrives.
/// </summary>
public sealed partial class AssignListAisle(
    IShoppingListRepository list, IShelfLifeEstimator jev, ShoppingListNotifier notify, TimeProvider clock,
    ILogger<AssignListAisle> logger)
{
    public const int MaxRetriesPerRun = 10;

    public async Task<bool> ExecuteAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var current = await list.GetAsync(itemId, cancellationToken);
        if (current?.Value is not { Removed: false } item || (item.Aisle is not null && item.AisleSource != ListAisleSources.Fallback))
        {
            return false; // gone, or it has an aisle already
        }

        string aisle, source;
        if (await list.GetMemoryAsync(item.NormalizedName, cancellationToken) is { Aisle: { } remembered })
        {
            (aisle, source) = (remembered, ListAisleSources.Jev);
        }
        else
        {
            var answer = await jev.ClassifyAsync(new ProductDescription(item.Name, null, null, []), null, aisle: true, icon: false, cancellationToken);
            if (answer is ClassificationResult.Classified { Classification.AisleId: { } chosen } && Aisles.Find(chosen) is not null)
            {
                (aisle, source) = (chosen, ListAisleSources.Jev);
                var memory = await list.GetMemoryAsync(item.NormalizedName, cancellationToken);
                await list.SaveMemoryAsync(
                    (memory ?? new ListMemory(item.NormalizedName, item.Name, null, 1, item.AddedAt)) with { Aisle = chosen }, cancellationToken);
            }
            else
            {
                LogJevFailed(itemId, answer.GetType().Name);
                if (item.AisleSource == ListAisleSources.Fallback)
                {
                    return false; // still Other; the timer tries again
                }

                (aisle, source) = (Aisles.Other.Id, ListAisleSources.Fallback);
            }
        }

        var updated = item.Changed($"aisle:{clock.GetUtcNow().ToUnixTimeMilliseconds()}", clock.GetUtcNow()) with { Aisle = aisle, AisleSource = source };
        if (!await list.TryReplaceAsync(current, updated, cancellationToken))
        {
            return false; // changed meanwhile: the next run (or the timer) tries again
        }

        LogAisle(itemId, aisle, source);
        await notify.ChangedAsync(updated, cancellationToken);
        return true;
    }

    /// <summary>The timer's retry: items still waiting, or put in Other because Jev was down.</summary>
    public async Task<int> RetryAsync(CancellationToken cancellationToken)
    {
        var waiting = (await list.ListAsync(cancellationToken))
            .Where(i => i.Value is { Removed: false } v && (v.Aisle is null || v.AisleSource == ListAisleSources.Fallback))
            .Take(MaxRetriesPerRun)
            .ToList();
        var done = 0;
        foreach (var item in waiting)
        {
            if (await ExecuteAsync(item.Value.Id, cancellationToken))
            {
                done++;
            }
        }

        return done;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shopping list: {ItemId} is in aisle {Aisle} ({Source}).")]
    private partial void LogAisle(Guid itemId, string aisle, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopping list: Jev couldn't give {ItemId} an aisle ({Outcome}); Other for now, retried by the timer.")]
    private partial void LogJevFailed(Guid itemId, string outcome);
}
