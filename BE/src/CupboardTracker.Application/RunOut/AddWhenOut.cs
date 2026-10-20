using System.Security.Cryptography;
using System.Text;
using CupboardTracker.Application.GenericNaming;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Push;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.RunOut;

/// <summary>
/// The products that count together for "running out" (#89): every known product with the same generic name, or, for a
/// product without one yet, every product with the same display name. <see cref="Record"/> is the generic name record
/// that holds the "add when we run out" setting (none for the display-name fallback, so it can't be turned on).
/// </summary>
public sealed record RunOutGroup(string Id, string Name, GenericName? Record, IReadOnlyList<Product> Products)
{
    public bool AddWhenOut => Record?.AddWhenOut == true;
}

/// <summary>Works out a product's <see cref="RunOutGroup"/> and how many units are in stock across it.</summary>
public sealed class RunOutGroups(IProductRepository products, IGenericNameRepository names, IUnitRepository units)
{
    public static string KeyOf(Product product) => product.Generic?.Id ?? GenericNames.IdOf(product.DisplayName);

    public async Task<RunOutGroup?> ForAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await products.GetAsync(productId, cancellationToken);
        if (product is null || product.Status != ProductStatus.Known)
        {
            return null;
        }

        var key = KeyOf(product);
        var members = (await products.ListKnownAsync(cancellationToken)).Where(p => KeyOf(p) == key).ToList();
        if (members.All(p => p.ProductId != product.ProductId))
        {
            members.Add(product);
        }

        var record = product.Generic is { } generic ? await names.GetAsync(generic.Id, cancellationToken) : null;
        return new RunOutGroup(key, product.Generic?.Name ?? product.DisplayName, record, members);
    }

    public async Task<int> InStockAsync(RunOutGroup group, CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var product in group.Products)
        {
            total += (await units.GetInStockAsync(product.ProductId, cancellationToken)).Count;
        }

        return total;
    }
}

/// <summary>
/// "Add to the shopping list when we run out" (#89). After every check-out (scanner or app, whatever its reason): if
/// the setting is on for the product's generic name and nothing is left in stock across every product with that name,
/// the name goes on the list with <c>source: autoRunOut</c>. The item's id and change id come from the unit and its
/// check-out time, so a replayed check-out never adds twice and its undo finds the same item. Undoing the check-out
/// takes the item off again, unless someone changed it (ticked, noted) since. Best-effort: a failure is a warning and
/// never fails the check-out.
/// </summary>
public sealed partial class AddWhenRunOut(
    RunOutGroups groups, AddToShoppingList add, ChangeShoppingListItem change, IShoppingListRepository list,
    ILogger<AddWhenRunOut> logger)
{
    /// <summary>Who an automatic add is "added by" (the list shows "Added when the last one was used" instead).</summary>
    public static readonly CurrentUser Cupboard = new(Guid.Empty, "Cupboard");

    public async Task CheckedOutAsync(Guid unitId, Guid productId, DateTimeOffset checkedOutAt, CancellationToken cancellationToken)
    {
        try
        {
            var group = await groups.ForAsync(productId, cancellationToken);
            if (group is not { AddWhenOut: true })
            {
                return;
            }

            var left = await groups.InStockAsync(group, cancellationToken);
            if (left > 0)
            {
                LogStillSome(group.Id, left);
                return;
            }

            var key = Key(unitId, checkedOutAt);
            var result = await add.ExecuteAsync(
                new AddListItem(ItemId(key), $"runout:{key}", group.Name, null, productId, ListItemSources.AutoRunOut), Cupboard, cancellationToken);
            switch (result)
            {
                case ListChangeResult.Changed { Merged: true }:
                    LogAlreadyOnList(group.Id);
                    break;
                case ListChangeResult.Changed added:
                    LogAdded(group.Id, added.Item.Id);
                    break;
                default:
                    LogNotAdded(group.Id, result.GetType().Name);
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(e, unitId);
        }
    }

    /// <summary>The check-out of <paramref name="unitId"/> at <paramref name="checkedOutAt"/> was undone.</summary>
    public async Task UndoneAsync(Guid unitId, DateTimeOffset checkedOutAt, CancellationToken cancellationToken)
    {
        try
        {
            var key = Key(unitId, checkedOutAt);
            var current = await list.GetAsync(ItemId(key), cancellationToken);
            if (current?.Value is not { Removed: false } item || item.Source != ListItemSources.AutoRunOut)
            {
                return; // nothing was added for this check-out
            }

            if (item.Status != ListItemStatus.Open || item.Note is not null || item.DoneBy is not null)
            {
                LogKept(item.Id); // ticked (even if put back) or given a note since: someone's using it
                return;
            }

            await change.RemoveAsync(item.Id, $"runout-undo:{key}", cancellationToken);
            LogRemoved(item.Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(e, unitId);
        }
    }

    /// <summary>One check-out: the unit and when it was checked out (a unit can be checked out, undone and checked out again).</summary>
    public static string Key(Guid unitId, DateTimeOffset checkedOutAt) =>
        $"{unitId:N}:{checkedOutAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The list item's id for a check-out: the same every time, so retries and undo agree.</summary>
    public static Guid ItemId(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"runout:{key}"))[..16]);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run out: {GenericNameId} added to the shopping list as {ItemId}.")]
    private partial void LogAdded(string genericNameId, Guid itemId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run out: {GenericNameId} still has {Left} in stock; not added.")]
    private partial void LogStillSome(string genericNameId, int left);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run out: {GenericNameId} is already on the shopping list.")]
    private partial void LogAlreadyOnList(string genericNameId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run out: {GenericNameId} couldn't be added ({Result}).")]
    private partial void LogNotAdded(string genericNameId, string result);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run out: check-out undone, so {ItemId} came off the shopping list.")]
    private partial void LogRemoved(Guid itemId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run out: check-out undone, but {ItemId} was changed since; left on the list.")]
    private partial void LogKept(Guid itemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run out: couldn't update the shopping list after a change to unit {UnitId}.")]
    private partial void LogFailed(Exception exception, Guid unitId);
}

/// <summary>Turns "add when we run out" on or off for a generic name (#89), for every product with that name.</summary>
public sealed class SetAddWhenOut(IGenericNameRepository names, TimeProvider clock)
{
    public Task<GenericName?> ExecuteAsync(string genericNameId, bool on, CurrentUser user, CancellationToken cancellationToken) =>
        names.SetAddWhenOutAsync(genericNameId, on, user.ObjectId, clock.GetUtcNow(), cancellationToken);
}

/// <summary>The run-out setting for item detail (#89): the generic name, whether it's on, and how many are in stock
/// across every product with that name. <see cref="CanSet"/> is false while the product has no generic name yet.</summary>
public sealed record RunOutStatus(string Id, string Name, bool AddWhenOut, int InStock, bool CanSet);

public sealed class GetRunOutStatus(RunOutGroups groups)
{
    public async Task<RunOutStatus?> ExecuteAsync(Guid productId, CancellationToken cancellationToken)
    {
        var group = await groups.ForAsync(productId, cancellationToken);
        return group is null
            ? null
            : new RunOutStatus(group.Id, group.Name, group.AddWhenOut, await groups.InStockAsync(group, cancellationToken), group.Record is not null);
    }
}

/// <summary>One row of the "Add to the shopping list?" sheet (#89).</summary>
public sealed record RunOutChoice(Guid ProductId, string GenericNameId, string Name, int Left, bool OnList, bool AddWhenOut, bool CanSet);

/// <summary>The sheet (#89): who checked out and when, and a row per removed generic item.</summary>
public sealed record RunOutChoices(string? CheckedOutBy, DateTimeOffset? At, IReadOnlyList<RunOutChoice> Items);

/// <summary>
/// What the "Add to the shopping list?" sheet shows (#89): for a tapped stock change push, the batch's removed items
/// (netted, so a check-out that was undone isn't offered, and not the caller's own check-outs); for a tapped
/// "Checked out" toast, that one product. One row per generic name, with how many are left and whether it's on the list.
/// </summary>
public sealed class GetRunOutChoices(IStockChangeBatchRepository batches, RunOutGroups groups, IShoppingListRepository list)
{
    public async Task<RunOutChoices?> ForBatchAsync(Guid batchId, CurrentUser user, CancellationToken cancellationToken)
    {
        var batch = await batches.GetSentAsync(batchId, cancellationToken);
        if (batch is null)
        {
            return null;
        }

        var theirs = batch.Changes.Where(c => c.ActorId != user.ObjectId).ToList();
        var products = theirs.Where(c => c.ProductId is not null)
            .GroupBy(c => c.ProductId!.Value)
            .Where(g => g.Sum(c => c.Kind == StockChangeKind.Removed ? 1 : -1) > 0) // netted: a check-out and its undo cancel
            .Select(g => g.Key).ToList();
        var lastOut = theirs.Where(c => c.Kind == StockChangeKind.Removed).MaxBy(c => c.At);
        var names = theirs.Where(c => c.Kind == StockChangeKind.Removed).Select(c => c.ActorName ?? (c.ActorId is null ? Scanner : null))
            .Distinct().ToList();
        return new RunOutChoices(names is [{ } only] ? only : null, lastOut?.At, await RowsAsync(products, cancellationToken));
    }

    public async Task<RunOutChoices> ForProductAsync(Guid productId, CancellationToken cancellationToken) =>
        new(null, null, await RowsAsync([productId], cancellationToken));

    /// <summary>Shown as "Checked out at the scanner".</summary>
    public const string Scanner = "the scanner";

    private async Task<IReadOnlyList<RunOutChoice>> RowsAsync(IReadOnlyList<Guid> productIds, CancellationToken cancellationToken)
    {
        var open = (await list.ListAsync(cancellationToken))
            .Where(i => i.Value is { Removed: false, Status: ListItemStatus.Open })
            .Select(i => i.Value.NormalizedName).ToHashSet();
        var rows = new List<RunOutChoice>();
        var seen = new HashSet<string>();
        foreach (var productId in productIds)
        {
            if (await groups.ForAsync(productId, cancellationToken) is not { } group || !seen.Add(group.Id))
            {
                continue;
            }

            rows.Add(new RunOutChoice(productId, group.Id, group.Name, await groups.InStockAsync(group, cancellationToken),
                open.Contains(ShoppingListNames.Normalize(group.Name)), group.AddWhenOut, group.Record is not null));
        }

        return rows;
    }
}
