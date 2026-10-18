using CupboardTracker.Application.Adding;
using CupboardTracker.Domain;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

/// <summary>Change-feed processing of one scan event: check-in, check-out, or a store-label check-in (#58).</summary>
public sealed partial class ProcessScanEvent(
    CheckInScan checkIn, CheckOutScan checkOut, StoreLabelCheckInScan storeLabelCheckIn, ILogger<ProcessScanEvent> logger)
{
    public async Task ExecuteAsync(ScanEvent scan, CancellationToken cancellationToken)
    {
        if (scan.Role == Role.In && StoreLabels.IsStoreLabel(scan.Barcode))
        {
            // #58: never looked up; waits for "what is it?". A store-label check-out goes through check-out and
            // becomes an unmatched check-out (its barcode never has a product).
            LogStoreLabel(scan.ScanId, scan.Barcode.Value);
            await storeLabelCheckIn.ExecuteAsync(scan, cancellationToken);
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
        Message = "Scan {ScanId} is a store-label check-in ({Barcode}): recorded, not looked up.")]
    private partial void LogStoreLabel(Guid scanId, string barcode);
}
