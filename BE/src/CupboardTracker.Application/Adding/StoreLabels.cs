using CupboardTracker.Application.Push;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Adding;

/// <summary>Store-label check-ins (#58), with optimistic concurrency.</summary>
public interface IStoreLabelRepository
{
    /// <summary>Records it (id from the scan ID). False if it already exists (a replay).</summary>
    Task<bool> CreateIfNotExistsAsync(StoreLabelCheckIn checkIn, CancellationToken cancellationToken);

    Task<Versioned<StoreLabelCheckIn>?> GetAsync(Guid scanId, CancellationToken cancellationToken);

    /// <summary>False if it changed since <paramref name="current"/> was read.</summary>
    Task<bool> TryReplaceAsync(Versioned<StoreLabelCheckIn> current, StoreLabelCheckIn updated, CancellationToken cancellationToken);

    /// <summary>Not yet assigned to a product.</summary>
    Task<IReadOnlyList<StoreLabelCheckIn>> ListUnresolvedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A check-in scan of an in-store printed label (#58): never looked up, no product created. It's recorded and shown
/// as "Store label: what is it?" until someone picks what it is (<see cref="AssignStoreLabel"/>).
/// </summary>
public sealed partial class StoreLabelCheckInScan(
    IStoreLabelRepository storeLabels, ScanFeedNotifier feed, TimeProvider clock, ILogger<StoreLabelCheckInScan> logger)
{
    public async Task ExecuteAsync(ScanEvent scan, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (await storeLabels.CreateIfNotExistsAsync(
                new StoreLabelCheckIn(scan.ScanId, scan.Barcode.Value, scan.DeviceId, scan.ScannedAt, now), cancellationToken))
        {
            LogRecorded(scan.ScanId, scan.Barcode.Value);
        }

        if (await storeLabels.GetAsync(scan.ScanId, cancellationToken) is { Value.IsResolved: false })
        {
            await feed.NotifyAsync(new StoreLabelScanned(scan.ScanId, scan.DeviceId, now, scan.Barcode.Value), cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Store-label check-in {ScanId} ({Barcode}) recorded: waiting for someone to say what it is.")]
    private partial void LogRecorded(Guid scanId, string barcode);
}

public abstract record AssignStoreLabelResult
{
    public sealed record Assigned(Guid ScanId, Guid UnitId, Guid ProductId, string DisplayName) : AssignStoreLabelResult;

    public sealed record Invalid(IReadOnlyDictionary<string, string> Errors) : AssignStoreLabelResult;

    public sealed record NotFound(Guid ScanId) : AssignStoreLabelResult;

    /// <summary>409: already assigned to a different product.</summary>
    public sealed record AlreadyAssigned(Guid ScanId, Guid ProductId) : AssignStoreLabelResult;
}

/// <summary>
/// "What is this?" (#58): assigns a store-label check-in to a product (existing, matched by name, or a new loose one)
/// and puts one unit in stock at the <b>original scan time</b>. The unit's id is the scan ID, like a scanned check-in,
/// so it's idempotent: assigning again to the same product changes nothing.
/// </summary>
public sealed partial class AssignStoreLabel(
    IStoreLabelRepository storeLabels,
    IUnitRepository units,
    AddToStock adder,
    StockSettings settings,
    ScanFeedNotifier feed,
    TimeProvider clock,
    ILogger<AssignStoreLabel> logger,
    AddStockChangeToPush? pushes = null)
{
    public async Task<AssignStoreLabelResult> ExecuteAsync(Guid scanId, AddItem item, CurrentUser user, CancellationToken cancellationToken)
    {
        var errors = AddToStock.Validate(scanId, [item with { Quantity = 1 }]);
        if (errors.Count > 0)
        {
            return new AssignStoreLabelResult.Invalid(errors);
        }

        var current = await storeLabels.GetAsync(scanId, cancellationToken);
        if (current is null)
        {
            return new AssignStoreLabelResult.NotFound(scanId);
        }

        var now = clock.GetUtcNow();
        var (product, _) = await adder.ResolveAsync(item, AddToStock.DeterministicId(scanId, "product"), user, now, cancellationToken);
        if (product is null)
        {
            return new AssignStoreLabelResult.Invalid(new Dictionary<string, string> { ["productId"] = "No such product." });
        }

        if (current.Value.ResolvedProductId is { } assigned && assigned != product.ProductId)
        {
            return new AssignStoreLabelResult.AlreadyAssigned(scanId, assigned);
        }

        var label = current.Value;
        var expiry = product.ShelfLife is { } shelfLife
            ? EstimatedExpiry.For(label.ScannedAt, shelfLife.Days, settings.HomeTimeZone)
            : (DateOnly?)null;
        var unit = new Unit(scanId, product.ProductId, label.Barcode, UnitStatus.InStock, scanId, label.DeviceId, label.ScannedAt,
            null, null, null, expiry, null, expiry is null ? null : ExpirySource.ShelfLife, user.ObjectId);
        if (await units.CreateIfNotExistsAsync(unit, cancellationToken) && pushes is not null)
        {
            // The unit appears in stock now (#81); the person who picked it isn't told.
            await pushes.ExecuteAsync(
                new StockChange(StockChangeKeys.ForApp(scanId, "added", now), StockChangeKind.Added, product.DisplayName, now,
                    product.ProductId, user.ObjectId),
                cancellationToken);
        }

        if (!label.IsResolved)
        {
            await storeLabels.TryReplaceAsync(current, label.Resolved(product.ProductId, user.ObjectId, now), cancellationToken);
            LogAssigned(scanId, product.ProductId, product.DisplayName);
        }

        await feed.NotifyAsync(new StoreLabelResolved(scanId, product.ProductId, product.DisplayName, now), cancellationToken);
        return new AssignStoreLabelResult.Assigned(scanId, scanId, product.ProductId, product.DisplayName);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Store-label check-in {ScanId} assigned to {ProductId} ({DisplayName}).")]
    private partial void LogAssigned(Guid scanId, Guid productId, string displayName);
}

public abstract record DismissStoreLabelResult
{
    public sealed record Dismissed(Guid ScanId) : DismissStoreLabelResult;

    public sealed record NotFound(Guid ScanId) : DismissStoreLabelResult;
}

/// <summary>Hides a store-label toast from the feed for everyone (#58). It stays on the scanners screen to pick later.</summary>
public sealed partial class DismissStoreLabelFromFeed(
    IStoreLabelRepository storeLabels, ScanFeedNotifier feed, TimeProvider clock, ILogger<DismissStoreLabelFromFeed> logger)
{
    public async Task<DismissStoreLabelResult> ExecuteAsync(Guid scanId, CurrentUser user, CancellationToken cancellationToken)
    {
        for (var round = 0; round < 3; round++)
        {
            var current = await storeLabels.GetAsync(scanId, cancellationToken);
            if (current is null)
            {
                return new DismissStoreLabelResult.NotFound(scanId);
            }

            var now = clock.GetUtcNow();
            if (await storeLabels.TryReplaceAsync(current, current.Value with { FeedDismissedAt = now }, cancellationToken))
            {
                LogDismissed(scanId, user.ObjectId);
                await feed.NotifyAsync(new FeedDismissed(null, now, scanId), cancellationToken);
                return new DismissStoreLabelResult.Dismissed(scanId);
            }
        }

        throw new InvalidOperationException($"Couldn't dismiss store-label check-in {scanId} after conflicting writes.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Store-label check-in {ScanId} dismissed from the feed by user {UserId}.")]
    private partial void LogDismissed(Guid scanId, Guid userId);
}
