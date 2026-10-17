using CupboardTracker.Domain;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

/// <summary>Change-feed processing of one scan event: check-in or check-out. Store labels are skipped for now.</summary>
public sealed partial class ProcessScanEvent(CheckInScan checkIn, CheckOutScan checkOut, ILogger<ProcessScanEvent> logger)
{
    public async Task ExecuteAsync(ScanEvent scan, CancellationToken cancellationToken)
    {
        if (StoreLabels.IsStoreLabel(scan.Barcode))
        {
            LogStoreLabel(scan.ScanId, scan.Barcode.Value);
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
