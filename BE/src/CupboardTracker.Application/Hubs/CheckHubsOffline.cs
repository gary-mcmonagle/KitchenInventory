using CupboardTracker.Application.Notifications;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Hubs;

/// <summary>
/// The offline check (every 5 minutes): emails once when a hub has been silent for longer than the threshold. The hub
/// is marked alerted <em>before</em> sending (a conditional write, so a heartbeat arriving at the same moment wins and
/// no email goes), and the mark is undone if sending fails, so the next run tries again. A hub that has never sent a
/// heartbeat has no record, so it never alerts.
/// </summary>
public sealed partial class CheckHubsOffline(
    IHubRepository hubs,
    INotifier notifier,
    OfflineAlertSettings settings,
    TimeProvider clock,
    ILogger<CheckHubsOffline> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        foreach (var current in await hubs.ListAsync(cancellationToken))
        {
            var hub = current.Value;
            var now = clock.GetUtcNow();
            if (!hub.NeedsOfflineAlert(now, settings.Threshold))
            {
                continue;
            }

            var alerted = hub with { OfflineAlertSentAt = now };
            if (!await hubs.TryReplaceAsync(current, alerted, cancellationToken))
            {
                LogChangedWhileChecking(hub.HubId);
                continue;
            }

            var email = OfflineAlertEmail.Build(hub.HubId, hub.LastSeenAt, now, settings.HomeTimeZone);
            try
            {
                await notifier.SendAsync(new Notification(NotificationKind.OfflineAlert, email), cancellationToken);
                LogAlertSent(hub.HubId, (int)(now - hub.LastSeenAt).TotalMinutes);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogSendFailed(e, hub.HubId);
                await UndoMarkAsync(hub, cancellationToken);
            }
        }
    }

    private async Task UndoMarkAsync(Domain.Hub hub, CancellationToken cancellationToken)
    {
        // Only if nothing else changed it since (a heartbeat would already have cleared it).
        var latest = await hubs.GetAsync(hub.HubId, cancellationToken);
        if (latest?.Value.OfflineAlertSentAt is not null)
        {
            await hubs.TryReplaceAsync(latest, latest.Value with { OfflineAlertSentAt = null }, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Hub {HubId} is offline (not seen for {Minutes} minutes): offline alert sent.")]
    private partial void LogAlertSent(string hubId, int minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hub {HubId} changed while being checked (a heartbeat arrived); no alert.")]
    private partial void LogChangedWhileChecking(string hubId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't send the offline alert for hub {HubId}; the next check will try again.")]
    private partial void LogSendFailed(Exception exception, string hubId);
}
