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
    HeartbeatDetails LastHeartbeat,
    IReadOnlyList<ScannerState>? ScannerStates = null)
{
    /// <summary>Each receiver's presence and since when (#55); empty until a heartbeat reports presence.</summary>
    public IReadOnlyList<ScannerState> Scanners => ScannerStates ?? [];

    /// <summary>
    /// The receivers' states after a heartbeat (#55). A device whose presence is unchanged keeps its <c>Since</c>; a
    /// change starts a new one at <paramref name="now"/> (server time). A heartbeat without a scanners list changes
    /// nothing, and devices it doesn't mention keep their last state.
    /// </summary>
    public static IReadOnlyList<ScannerState> NextScannerStates(
        IReadOnlyList<ScannerState> previous, IReadOnlyList<ScannerPresence>? reported, DateTimeOffset now)
    {
        if (reported is null)
        {
            return previous;
        }

        var reportedStates = reported.Select(r =>
            previous.FirstOrDefault(p => p.DeviceId == r.DeviceId) is { } p && p.Present == r.Present
                ? p
                : new ScannerState(r.DeviceId, r.Present, now));
        var unmentioned = previous.Where(p => reported.All(r => r.DeviceId != p.DeviceId));
        return [.. reportedStates, .. unmentioned];
    }

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

/// <summary>A receiver's presence and since when (server time): "Connected", or "Unplugged since …" (#55). Only says
/// the receiver is plugged into the hub; the hub can't tell whether a wireless scanner is on or in range.</summary>
public sealed record ScannerState(string DeviceId, bool Present, DateTimeOffset Since);
