namespace CupboardTracker.Domain.Stock;

public enum UnitStatus
{
    InStock,
    CheckedOut,
}

/// <summary>Where a unit's estimated expiry date came from (#53).</summary>
public enum ExpirySource
{
    /// <summary>The bought date plus the product's shelf life (#52).</summary>
    ShelfLife,

    /// <summary>Set by a person in item detail. Never recalculated.</summary>
    Manual,
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
    Guid? CheckedOutBy = null,
    ExpirySource? ExpirySource = null,
    Guid? CheckedInBy = null)
{
    /// <summary>A unit added in the app (#58): no scan, so no scan or device; the user who added it instead.</summary>
    public static Unit AddedInApp(Guid unitId, Guid productId, Guid userId, DateTimeOffset at, DateOnly? estimatedExpiry) =>
        new(unitId, productId, null, UnitStatus.InStock, null, null, at, null, null, null, estimatedExpiry, null,
            estimatedExpiry is null ? null : Stock.ExpirySource.ShelfLife, userId);

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

    public Unit WithEstimatedExpiry(DateOnly date) =>
        this with { EstimatedExpiryDate = date, ExpirySource = Stock.ExpirySource.ShelfLife };

    /// <summary>A date a person set (#53): this unit only, and never recalculated.</summary>
    public Unit WithManualExpiry(DateOnly date) =>
        this with { EstimatedExpiryDate = date, ExpirySource = Stock.ExpirySource.Manual };

    /// <summary>Back in stock, as if it had never been checked out.</summary>
    public Unit UndoCheckOut() =>
        this with
        {
            Status = UnitStatus.InStock, CheckedOutAt = null, CheckOutScanId = null, CheckOutSource = null, CheckedOutBy = null,
        };
}

/// <summary>A check-out scan with no unit in stock to remove (unknown barcode, or nothing in stock).</summary>
public sealed record UnmatchedCheckOut(
    Guid ScanId, string Barcode, Guid? ProductId, string DeviceId, DateTimeOffset ScannedAt, DateTimeOffset RecordedAt,
    DateTimeOffset? DismissedAt = null, Guid? DismissedBy = null)
{
    /// <summary>Dismissed on the scanners screen (#55): reviewed, nothing to do. Stays dismissed.</summary>
    public bool IsDismissed => DismissedAt is not null;
}
