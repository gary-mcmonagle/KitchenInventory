using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Domain;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

/// <summary>Change-feed processing of one scan event: check-in or check-out. Store labels are skipped for now.</summary>
public sealed partial class ProcessScanEvent(
    CheckInScan checkIn, CheckOutScan checkOut, ScanFeedNotifier feed, TimeProvider clock, ILogger<ProcessScanEvent> logger)
{
    public async Task ExecuteAsync(ScanEvent scan, CancellationToken cancellationToken)
    {
        if (StoreLabels.IsStoreLabel(scan.Barcode))
        {
            LogStoreLabel(scan.ScanId, scan.Barcode.Value);
            await feed.NotifyAsync(
                new ScanSkipped(scan.ScanId, scan.DeviceId, scan.Role, clock.GetUtcNow(), scan.Barcode.Value, ScanSkipped.StoreLabel),
                cancellationToken);
            return;
        }

        if (scan.Role == Role.In)
        {
            await checkIn.ExecuteAsync(scan, cancellationToken);
        }
        else
        {
            await checkOut.ExecuteAsync(scan, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Scan {ScanId} is a store label ({Barcode}): skipped until loose and store-labelled items are supported.")]
    private partial void LogStoreLabel(Guid scanId, string barcode);
}
