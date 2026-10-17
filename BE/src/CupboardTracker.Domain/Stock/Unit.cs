namespace CupboardTracker.Domain.Stock;

public enum UnitStatus
{
    InStock,
    CheckedOut,
}

/// <summary>One physical item in the house. Created on check-in (id = the check-in scan ID); checked out, never deleted.</summary>
public sealed record Unit(
    Guid UnitId,
    Guid ProductId,
    string? Barcode,
    UnitStatus Status,
    Guid? CheckInScanId,
    string? CheckInDeviceId,
    DateTimeOffset CheckedInAt,
    DateTimeOffset? CheckedOutAt,
    Guid? CheckOutScanId,
    string? CheckOutSource,
    DateOnly? EstimatedExpiryDate)
{
    public Unit CheckOut(Guid scanId, DateTimeOffset at, string source) =>
        this with { Status = UnitStatus.CheckedOut, CheckedOutAt = at, CheckOutScanId = scanId, CheckOutSource = source };
}

/// <summary>A check-out scan with no unit in stock to remove (unknown barcode, or nothing in stock).</summary>
public sealed record UnmatchedCheckOut(
    Guid ScanId, string Barcode, Guid? ProductId, string DeviceId, DateTimeOffset ScannedAt, DateTimeOffset RecordedAt);
