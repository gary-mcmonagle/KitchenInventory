using System.Text.RegularExpressions;

namespace CupboardTracker.Application.ShoppingList;

public enum ListItemStatus
{
    Open,
    Done,
}

/// <summary>Where a list item came from (#84). Later cards add more (e.g. run-out adds).</summary>
public static class ListItemSources
{
    public const string Typed = "typed";
    public const string Suggestion = "suggestion";
    public const string ItemDetail = "itemDetail";

    /// <summary>"Add to the shopping list?" after a check-out (#89).</summary>
    public const string CheckOutPrompt = "checkOutPrompt";

    /// <summary>Added by itself when the house ran out (#89). Only the server adds these.</summary>
    public const string AutoRunOut = "autoRunOut";

    /// <summary>"Add missing to the list" on a saved recipe (#105).</summary>
    public const string SavedRecipe = "savedRecipe";

    /// <summary>"Add "…" to the shopping list" under an empty stock search (#97).</summary>
    public const string StockSearch = "stockSearch";

    public static readonly IReadOnlySet<string> FromTheApp = new HashSet<string>(StringComparer.Ordinal) { Typed, Suggestion, ItemDetail, CheckOutPrompt, SavedRecipe, StockSearch };
}

/// <summary>How a list item got its aisle: from its product, from Jev (or a remembered Jev answer), or Other because
/// Jev couldn't be reached (retried by the timer).</summary>
public static class ListAisleSources
{
    public const string Product = "product";
    public const string Jev = "jev";
    public const string Fallback = "fallback";
}

/// <summary>
/// One thing on the household's shopping list (#84). <see cref="Id"/> and every change's <see cref="ChangeId"/> come from
/// the app, so a retried write (or one sent again after being offline) is applied once. <see cref="Version"/> goes up on
/// every change; apps keep the higher one. A removed item stays a while as a tombstone (<see cref="Removed"/>), so apps
/// catching up learn it's gone. <see cref="StatusChangedAt"/> is when the last tick or untick was made (on the phone,
/// #85), so a change sent late from an offline phone can't undo a newer one.
/// </summary>
public sealed record ShoppingListItem(
    Guid Id,
    string Name,
    string NormalizedName,
    string? Note,
    Guid? ProductId,
    string? Aisle,
    string? AisleSource,
    string Source,
    ListItemStatus Status,
    Guid AddedBy,
    string AddedByName,
    DateTimeOffset AddedAt,
    Guid? DoneBy,
    string? DoneByName,
    DateTimeOffset? DoneAt,
    DateTimeOffset UpdatedAt,
    long Version,
    string ChangeId,
    bool Removed = false,
    DateTimeOffset? StatusChangedAt = null)
{
    /// <summary>Waiting for Jev to say which aisle (the app shows a spinner).</summary>
    public bool FindingAisle => Aisle is null && !Removed;

    public ShoppingListItem Changed(string changeId, DateTimeOffset at) => this with { ChangeId = changeId, UpdatedAt = at, Version = Version + 1 };
}

/// <summary>What the household has put on the list before, by name: the aisle Jev gave it (reused without asking
/// again) and how often and when it was added (for suggestions).</summary>
public sealed record ListMemory(string NormalizedName, string Name, string? Aisle, int TimesAdded, DateTimeOffset LastAddedAt);

public static partial class ShoppingListNames
{
    public const int MaxName = 80;
    public const int MaxNote = 60;

    /// <summary>Lower case, trimmed, single spaces: two names that normalize the same are the same item.</summary>
    public static string Normalize(string name) => Spaces().Replace(name.Trim(), " ").ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>The shoppingList container (one household list).</summary>
public interface IShoppingListRepository
{
    Task<Versioned<ShoppingListItem>?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every item, including tombstones.</summary>
    Task<IReadOnlyList<Versioned<ShoppingListItem>>> ListAsync(CancellationToken cancellationToken);

    /// <summary>False if an item with that id exists already.</summary>
    Task<bool> TryCreateAsync(ShoppingListItem item, CancellationToken cancellationToken);

    /// <summary>False if it changed since it was read.</summary>
    Task<bool> TryReplaceAsync(Versioned<ShoppingListItem> current, ShoppingListItem updated, CancellationToken cancellationToken);

    Task<ListMemory?> GetMemoryAsync(string normalizedName, CancellationToken cancellationToken);

    Task<IReadOnlyList<ListMemory>> ListMemoryAsync(CancellationToken cancellationToken);

    Task SaveMemoryAsync(ListMemory memory, CancellationToken cancellationToken);
}

/// <summary>Live list updates to every open app (the scan feed's SignalR hub in Infrastructure). May throw: use
/// <see cref="ShoppingListNotifier"/>.</summary>
public interface IShoppingListPublisher
{
    Task ItemChangedAsync(ShoppingListItem item, CancellationToken cancellationToken);

    Task ItemRemovedAsync(Guid id, long version, CancellationToken cancellationToken);

    Task ClearedAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken);
}

/// <summary>Works out a typed item's aisle after the add returns (a Storage queue message in Infrastructure).</summary>
public interface IListAisleScheduler
{
    Task ScheduleAsync(Guid itemId, CancellationToken cancellationToken);
}
