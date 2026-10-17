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
    DateOnly? EstimatedExpiryDate,
    Guid? CheckedOutBy = null)
{
    /// <summary><see cref="CheckOutSource"/> for check-outs from the stock list (scanner check-outs use "scanner").</summary>
    public const string AppSource = "ui";

    public Unit CheckOut(Guid scanId, DateTimeOffset at, string source) =>
        this with { Status = UnitStatus.CheckedOut, CheckedOutAt = at, CheckOutScanId = scanId, CheckOutSource = source };

    /// <summary>Checked out from the stock list by a signed-in user (no scan).</summary>
    public Unit CheckOutByUser(Guid userId, DateTimeOffset at) =>
        this with
        {
            Status = UnitStatus.CheckedOut, CheckedOutAt = at, CheckOutScanId = null, CheckOutSource = AppSource,
            CheckedOutBy = userId,
        };

    /// <summary>Back in stock, as if it had never been checked out.</summary>
    public Unit UndoCheckOut() =>
        this with
        {
            Status = UnitStatus.InStock, CheckedOutAt = null, CheckOutScanId = null, CheckOutSource = null, CheckedOutBy = null,
        };
}

/// <summary>A check-out scan with no unit in stock to remove (unknown barcode, or nothing in stock).</summary>
public sealed record UnmatchedCheckOut(
    Guid ScanId, string Barcode, Guid? ProductId, string DeviceId, DateTimeOffset ScannedAt, DateTimeOffset RecordedAt);
