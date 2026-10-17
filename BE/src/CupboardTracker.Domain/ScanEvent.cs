namespace CupboardTracker.Domain;

/// <summary>
/// A single raw scan sent by the hub (or the simulator). Append-only: never edited or deleted.
/// Its <see cref="ScanId"/> is minted by the client when the barcode is read, so a resend is the same scan.
/// </summary>
public sealed record ScanEvent(
    Guid ScanId,
    Barcode Barcode,
    string DeviceId,
    Role Role,
    DateTimeOffset ScannedAt,
    DateTimeOffset ReceivedAt,
    string ClientSha);
