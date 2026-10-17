namespace CupboardTracker.Domain;

/// <summary>The logical identity the hub gives a scanner, and its role. Created or updated by ingest.</summary>
/// <param name="LastSeenAt">Server receive time of the last scan (server time, so a wrong Pi clock can't hide an outage).</param>
/// <param name="LastScanAt"><c>scannedAt</c> of the latest scan.</param>
public sealed record Device(string DeviceId, Role Role, DateTimeOffset LastSeenAt, DateTimeOffset? LastScanAt);
