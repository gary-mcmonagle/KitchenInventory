using CupboardTracker.Domain;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Hubs;

/// <summary>A heartbeat as sent by the hub, before validation. See docs/api.md.</summary>
public sealed record RecordHeartbeatCommand(
    string? HubId,
    string? SentAt,
    string? ClientSha,
    long? UptimeSeconds,
    IReadOnlyList<string>? UsbInputDevices,
    IReadOnlyList<ScannerPresenceCommand>? Scanners);

public sealed record ScannerPresenceCommand(string? DeviceId, bool? Present);

public abstract record RecordHeartbeatResult
{
    public sealed record Recorded(string HubId) : RecordHeartbeatResult;

    public sealed record Invalid(IReadOnlyList<string> Errors) : RecordHeartbeatResult;
}

/// <summary>
/// Records a heartbeat: the hub's <c>lastSeenAt</c> becomes the server's receive time, and its latest details are
/// stored. A heartbeat also ends an outage: the offline-alert flag is cleared (no email), so the next outage alerts
/// again.
/// </summary>
public sealed partial class RecordHeartbeat(IHubRepository hubs, TimeProvider clock, ILogger<RecordHeartbeat> logger)
{
    public const int MaxClientShaLength = 64;
    public const int MaxUsbInputDevices = 32;
    public const int MaxUsbInputDeviceNameLength = 200;
    public const int MaxScanners = 16;
    private const int MaxAttempts = 5;

    public async Task<RecordHeartbeatResult> ExecuteAsync(RecordHeartbeatCommand command, CancellationToken cancellationToken)
    {
        var errors = Validate(command, out var details);
        if (errors.Count > 0)
        {
            return new RecordHeartbeatResult.Invalid(errors);
        }

        var hubId = command.HubId!;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var now = clock.GetUtcNow();
            var hub = new Hub(hubId, LastSeenAt: now, OfflineAlertSentAt: null, details!);
            var existing = await hubs.GetAsync(hubId, cancellationToken);
            if (existing is null)
            {
                if (await hubs.TryCreateAsync(hub, cancellationToken))
                {
                    LogFirstHeartbeat(hubId);
                    return new RecordHeartbeatResult.Recorded(hubId);
                }
            }
            else if (await hubs.TryReplaceAsync(existing, hub, cancellationToken))
            {
                if (existing.Value.OfflineAlertSentAt is not null)
                {
                    LogBackOnline(hubId, (int)Math.Round((now - existing.Value.LastSeenAt).TotalMinutes));
                }

                return new RecordHeartbeatResult.Recorded(hubId);
            }

            // Changed under us (e.g. the offline check marked it alerted): read again and retry.
        }

        throw new InvalidOperationException($"Hub {hubId} kept changing; couldn't record the heartbeat.");
    }

    private static List<string> Validate(RecordHeartbeatCommand command, out HeartbeatDetails? details)
    {
        details = null;
        var errors = new List<string>();
        if (!DeviceIds.IsValid(command.HubId))
        {
            errors.Add("hubId is required: lowercase letters, digits and dashes.");
        }

        if (!Timestamps.TryParseWithOffset(command.SentAt, out var sentAt))
        {
            errors.Add("sentAt must be an ISO 8601 timestamp with a timezone (e.g. 2026-10-16T18:42:07.123Z).");
        }

        if (string.IsNullOrWhiteSpace(command.ClientSha) || command.ClientSha.Length > MaxClientShaLength)
        {
            errors.Add($"clientSha is required (at most {MaxClientShaLength} characters).");
        }

        if (command.UptimeSeconds is not >= 0)
        {
            errors.Add("uptimeSeconds is required: a whole number, 0 or more.");
        }

        if (command.UsbInputDevices is null)
        {
            errors.Add("usbInputDevices is required: a list of names (may be empty).");
        }
        else if (command.UsbInputDevices.Count > MaxUsbInputDevices
            || command.UsbInputDevices.Any(n => n.Length > MaxUsbInputDeviceNameLength))
        {
            errors.Add($"usbInputDevices: at most {MaxUsbInputDevices} names of at most {MaxUsbInputDeviceNameLength} characters.");
        }

        if (command.Scanners is { } scanners)
        {
            if (scanners.Count > MaxScanners)
            {
                errors.Add($"scanners: at most {MaxScanners} entries.");
            }

            if (scanners.Any(s => !DeviceIds.IsValid(s.DeviceId) || s.Present is null))
            {
                errors.Add("Each scanners entry needs a deviceId (lowercase letters, digits and dashes) and present (true or false).");
            }
        }

        if (errors.Count == 0)
        {
            details = new HeartbeatDetails(
                sentAt.ToUniversalTime(),
                command.ClientSha!,
                command.UptimeSeconds!.Value,
                command.UsbInputDevices!,
                command.Scanners?.Select(s => new ScannerPresence(s.DeviceId!, s.Present!.Value)).ToList());
        }

        return errors;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "First heartbeat from hub {HubId}.")]
    private partial void LogFirstHeartbeat(string hubId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hub {HubId} back online after {Minutes} minutes; offline alert cleared (no email).")]
    private partial void LogBackOnline(string hubId, int minutes);
}
