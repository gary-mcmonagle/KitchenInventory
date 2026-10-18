namespace CupboardTracker.Domain.Stock;

/// <summary>
/// A check-in scan of an in-store printed label (#58): deli or weighed items, whose barcode differs on every pack, so
/// it's never looked up. It waits for someone to say what it is ("Pick"); assigning it creates the unit at the scan's
/// time, with the unit's id = the scan ID.
/// </summary>
public sealed record StoreLabelCheckIn(
    Guid ScanId,
    string Barcode,
    string DeviceId,
    DateTimeOffset ScannedAt,
    DateTimeOffset RecordedAt,
    DateTimeOffset? ResolvedAt = null,
    Guid? ResolvedProductId = null,
    Guid? ResolvedBy = null,
    DateTimeOffset? FeedDismissedAt = null)
{
    public bool IsResolved => ResolvedAt is not null;

    public StoreLabelCheckIn Resolved(Guid productId, Guid userId, DateTimeOffset at) =>
        this with { ResolvedAt = at, ResolvedProductId = productId, ResolvedBy = userId };
}
