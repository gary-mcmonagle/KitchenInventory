using CupboardTracker.Application.Products;
using CupboardTracker.Application.Push;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

/// <summary>
/// A check-out scan takes one in-stock unit of the barcode's product (chosen by <see cref="UnitSelection"/>) and marks it
/// checked out. No unit to take → an unmatched check-out. Replays do nothing; concurrent check-outs take different units.
/// A check-out joins the stock change push (#80).
/// </summary>
public sealed partial class CheckOutScan(
    IProductRepository products,
    IUnitRepository units,
    IUnmatchedCheckOutRepository unmatched,
    ScanFeedNotifier feed,
    TimeProvider clock,
    ILogger<CheckOutScan> logger,
    AddStockChangeToPush? pushes = null)
{
    public const string Source = "scanner";
    private const int MaxRounds = 5;

    public async Task ExecuteAsync(ScanEvent scan, CancellationToken cancellationToken)
    {
        if (await unmatched.ExistsAsync(scan.ScanId, cancellationToken))
        {
            LogReplay(scan.ScanId);
            return;
        }

        var productId = await products.GetProductIdForBarcodeAsync(scan.Barcode.Value, cancellationToken);
        if (productId is null)
        {
            await RecordUnmatchedAsync(scan, null, cancellationToken); // a barcode we've never seen
            return;
        }

        if (await units.IsCheckedOutByScanAsync(productId.Value, scan.ScanId, cancellationToken))
        {
            LogReplay(scan.ScanId);
            return;
        }

        for (var round = 0; round < MaxRounds; round++)
        {
            var inStock = await units.GetInStockAsync(productId.Value, cancellationToken);
            if (inStock.Count == 0)
            {
                await RecordUnmatchedAsync(scan, productId, cancellationToken); // known product, nothing in stock
                return;
            }

            var byId = inStock.ToDictionary(v => v.Value.UnitId);
            foreach (var candidate in UnitSelection.InCheckOutOrder(inStock.Select(v => v.Value)))
            {
                var current = byId[candidate.UnitId];
                if (await units.TryReplaceAsync(current, candidate.CheckOut(scan.ScanId, scan.ScannedAt, Source), cancellationToken))
                {
                    LogCheckedOut(scan.ScanId, candidate.UnitId, scan.Barcode.Value);
                    var product = await products.GetAsync(productId.Value, cancellationToken);
                    var name = product?.DisplayName ?? Product.UnknownName(scan.Barcode.Value);
                    await feed.NotifyAsync(
                        new ScanIdentified(scan.ScanId, scan.DeviceId, scan.Role, clock.GetUtcNow(), ScanOutcome.CheckedOut,
                            productId.Value, name, product?.Details?.Brand, product?.Details?.Size, candidate.UnitId),
                        cancellationToken);
                    if (pushes is not null)
                    {
                        await pushes.ExecuteAsync(
                            new StockChange(StockChangeKeys.ForScan(scan.ScanId), StockChangeKind.Removed, name, clock.GetUtcNow()),
                            cancellationToken);
                    }

                    return;
                }

                LogConflict(candidate.UnitId, scan.ScanId); // taken by a concurrent check-out: try the next one
            }
        }

        throw new InvalidOperationException(
            $"Couldn't check out a unit for scan {scan.ScanId} after {MaxRounds} rounds of conflicts.");
    }

    private async Task RecordUnmatchedAsync(ScanEvent scan, Guid? productId, CancellationToken cancellationToken)
    {
        var record = new UnmatchedCheckOut(
            scan.ScanId, scan.Barcode.Value, productId, scan.DeviceId, scan.ScannedAt, clock.GetUtcNow());
        if (await unmatched.CreateIfNotExistsAsync(record, cancellationToken))
        {
            LogUnmatched(scan.ScanId, scan.Barcode.Value);
        }

        var product = productId is { } id ? await products.GetAsync(id, cancellationToken) : null;
        await feed.NotifyAsync(
            new CheckOutUnmatched(scan.ScanId, scan.DeviceId, clock.GetUtcNow(), scan.Barcode.Value, productId,
                product is { IsUnnamed: false } ? product.DisplayName : null),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Checked out unit {UnitId} for scan {ScanId} ({Barcode}).")]
    private partial void LogCheckedOut(Guid scanId, Guid unitId, string barcode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unmatched check-out {ScanId}: nothing in stock for {Barcode}.")]
    private partial void LogUnmatched(Guid scanId, string barcode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Check-out {ScanId} already processed (replay): nothing changed.")]
    private partial void LogReplay(Guid scanId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unit {UnitId} changed under check-out {ScanId}; trying the next unit.")]
    private partial void LogConflict(Guid unitId, Guid scanId);
}
