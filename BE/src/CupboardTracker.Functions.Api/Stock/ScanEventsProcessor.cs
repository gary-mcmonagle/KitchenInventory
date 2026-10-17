using CupboardTracker.Application.Stock;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>
/// Change-feed processing of new scan events: check-in adds a unit, check-out removes one (docs/data-model.md).
/// Starts from deploy time (no backfill). Every step is idempotent, so the retry policy and feed replays are safe.
/// </summary>
public sealed partial class ScanEventsProcessor(ProcessScanEvent process, ILogger<ScanEventsProcessor> logger)
{
    [Function("scanEventsChangeFeed")]
    [ExponentialBackoffRetry(5, "00:00:05", "00:05:00")]
    public async Task Run(
        [CosmosDBTrigger(
            databaseName: "%COSMOS_DATABASE%",
            containerName: "scanEvents",
            Connection = "COSMOS",
            LeaseContainerName = "leases",
            CreateLeaseContainerIfNotExists = false,
            StartFromBeginning = false)]
        IReadOnlyList<ScanEventDocument> documents,
        CancellationToken cancellationToken)
    {
        foreach (var document in documents)
        {
            if (document.ToScanEvent() is { } scan)
            {
                await process.ExecuteAsync(scan, cancellationToken);
            }
            else
            {
                LogInvalid(document.Id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scan event {Id} in the change feed isn't valid; skipped.")]
    private partial void LogInvalid(string? id);
}
