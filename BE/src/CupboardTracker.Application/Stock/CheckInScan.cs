using CupboardTracker.Application.Products;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

/// <summary>
/// A check-in scan becomes one unit in stock (id = scan ID, so replays are no-ops), identified as a product. If the
/// product already has a shelf life, the unit gets its estimated expiry date now; otherwise the shelf-life timer fills
/// it in later (#52), so check-in never waits for Jev.
/// </summary>
public sealed partial class CheckInScan(
    ProductResolver resolver,
    IUnitRepository units,
    StockSettings settings,
    ScanFeedNotifier feed,
    TimeProvider clock,
    ILogger<CheckInScan> logger)
{
    public async Task ExecuteAsync(ScanEvent scan, CancellationToken cancellationToken)
    {
        var product = await resolver.ResolveAsync(scan.Barcode.Value, cancellationToken);
        var unit = new Unit(
            scan.ScanId, product.ProductId, scan.Barcode.Value, UnitStatus.InStock, scan.ScanId, scan.DeviceId,
            scan.ScannedAt, null, null, null,
            product.ShelfLife is { } shelfLife
                ? EstimatedExpiry.For(scan.ScannedAt, shelfLife.Days, settings.HomeTimeZone)
                : null);

        if (await units.CreateIfNotExistsAsync(unit, cancellationToken))
        {
            LogCheckedIn(scan.ScanId, scan.Barcode.Value, product.DisplayName);
        }
        else
        {
            LogReplay(scan.ScanId);
        }

        // Also on a replay: the app updates the toast with this scan ID in place (spike #38).
        await feed.NotifyAsync(
            product.IsUnnamed
                ? new ScanNeedsIdentification(scan.ScanId, scan.DeviceId, clock.GetUtcNow(), product.ProductId,
                    scan.Barcode.Value, product.DisplayName, product.Status, unit.UnitId)
                : new ScanIdentified(scan.ScanId, scan.DeviceId, scan.Role, clock.GetUtcNow(), ScanOutcome.CheckedIn,
                    product.ProductId, product.DisplayName, product.Details?.Brand, product.Details?.Size, unit.UnitId),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Checked in {ScanId}: {Barcode} as {DisplayName}.")]
    private partial void LogCheckedIn(Guid scanId, string barcode, string displayName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Check-in {ScanId} already processed (replay): no new unit.")]
    private partial void LogReplay(Guid scanId);
}
