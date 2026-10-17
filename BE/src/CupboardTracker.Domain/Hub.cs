namespace CupboardTracker.Domain;

/// <summary>
/// The hub: the Raspberry Pi the scanners connect to. It sends scan events and heartbeats. Stored in the devices
/// container with <c>kind: "hub"</c> (docs/data-model.md).
/// </summary>
/// <param name="LastSeenAt">Server receive time of the latest heartbeat (never the Pi's clock, so a wrong clock can't hide
/// an outage).</param>
/// <param name="OfflineAlertSentAt">When the offline alert for the current outage was sent; null while online. Cleared by
/// the next heartbeat, so each outage sends one email.</param>
public sealed record Hub(
    string HubId,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? OfflineAlertSentAt,
    HeartbeatDetails LastHeartbeat)
{
    /// <summary>Not seen for longer than the threshold (exactly the threshold isn't offline yet).</summary>
    public bool IsOffline(DateTimeOffset now, TimeSpan threshold) => now - LastSeenAt > threshold;

    /// <summary>Offline, and nobody has been told about this outage yet.</summary>
    public bool NeedsOfflineAlert(DateTimeOffset now, TimeSpan threshold) =>
        OfflineAlertSentAt is null && IsOffline(now, threshold);
}

/// <summary>What the hub reported in its latest heartbeat (informational).</summary>
public sealed record HeartbeatDetails(
    DateTimeOffset SentAt,
    string ClientSha,
    long UptimeSeconds,
    IReadOnlyList<string> UsbInputDevices,
    IReadOnlyList<ScannerPresence>? Scanners);

/// <summary>Whether a scanner's dongle is plugged into the hub, as the hub reports it.</summary>
public sealed record ScannerPresence(string DeviceId, bool Present);
