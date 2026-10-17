using CupboardTracker.Application.Products;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

/// <summary>A check-in scan becomes one unit in stock (id = scan ID, so replays are no-ops), identified as a product.</summary>
public sealed partial class CheckInScan(ProductResolver resolver, IUnitRepository units, ILogger<CheckInScan> logger)
{
    public async Task ExecuteAsync(ScanEvent scan, CancellationToken cancellationToken)
    {
        var product = await resolver.ResolveAsync(scan.Barcode.Value, cancellationToken);
        var unit = new Unit(
            scan.ScanId, product.ProductId, scan.Barcode.Value, UnitStatus.InStock, scan.ScanId, scan.DeviceId,
            scan.ScannedAt, null, null, null, null);

        if (await units.CreateIfNotExistsAsync(unit, cancellationToken))
        {
            LogCheckedIn(scan.ScanId, scan.Barcode.Value, product.DisplayName);
        }
        else
        {
            LogReplay(scan.ScanId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Checked in {ScanId}: {Barcode} as {DisplayName}.")]
    private partial void LogCheckedIn(Guid scanId, string barcode, string displayName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Check-in {ScanId} already processed (replay): no new unit.")]
    private partial void LogReplay(Guid scanId);
}
