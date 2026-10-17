using CupboardTracker.Application.Hubs;
using Microsoft.Azure.Functions.Worker;

namespace CupboardTracker.Functions.Api.Hubs;

/// <summary>Every 5 minutes: email once if a hub has been silent longer than OFFLINE_THRESHOLD_MINUTES (#44).</summary>
public sealed class OfflineCheckTimer(CheckHubsOffline check)
{
    [Function("offlineCheck")]
    public Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        check.ExecuteAsync(cancellationToken);
}
