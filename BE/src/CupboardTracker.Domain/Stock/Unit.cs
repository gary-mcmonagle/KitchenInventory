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

    /// <summary>The earlier of the open date plus the product's "once opened" shelf life and the estimate before (#65).</summary>
    Opened,

    /// <summary>The freeze date plus the product's freezer shelf life (#65).</summary>
    Frozen,

    /// <summary>The defrost date plus the product's "once defrosted" shelf life (#65).</summary>
    Defrosted,
}

/// <summary>
/// A unit's estimate fields as they were before a state change, so Undo puts them back exactly (#65). <see cref="Before"/>
/// is the snapshot before that, so each change can be undone in turn (froze it, defrosted, then Undo, Undo), up to
/// <see cref="MaxDepth"/> back.
/// </summary>
public sealed record UnitEstimateSnapshot(
    DateOnly? EstimatedExpiryDate,
    ExpirySource? ExpirySource,
    DateOnly? OpenedOn,
    DateOnly? FrozenOn,
    DateOnly? DefrostedOn,
    Products.Storage? CurrentStorage,
    bool EstimatePending,
    UnitEstimateSnapshot? Before = null)
{
    public const int MaxDepth = 5;

    /// <summary>This snapshot with at most <paramref name="depth"/> levels kept.</summary>
    public UnitEstimateSnapshot Trimmed(int depth = MaxDepth) =>
        this with { Before = depth <= 1 ? null : Before?.Trimmed(depth - 1) };
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
    Guid? CheckedInBy = null,
    DateOnly? OpenedOn = null,
    DateOnly? FrozenOn = null,
    DateOnly? DefrostedOn = null,
    Products.Storage? CurrentStorage = null,
    bool EstimatePending = false,
    UnitEstimateSnapshot? BeforeStateChange = null,
    DateTimeOffset? LastConfirmedAt = null,
    string? LastConfirmedVia = null,
    string? CheckOutReason = null)
{
    public bool IsOpened => OpenedOn is not null;

    /// <summary>In the freezer now: frozen and not defrosted since.</summary>
    public bool IsFrozen => FrozenOn is not null && DefrostedOn is null;

    public UnitEstimateSnapshot Snapshot() =>
        new UnitEstimateSnapshot(
            EstimatedExpiryDate, ExpirySource, OpenedOn, FrozenOn, DefrostedOn, CurrentStorage, EstimatePending, BeforeStateChange)
            .Trimmed();

    /// <summary>Back to the estimate fields before the last state change (#65); the one before that can be undone next.</summary>
    public Unit UndoStateChange() =>
        BeforeStateChange is not { } before
            ? this
            : this with
            {
                EstimatedExpiryDate = before.EstimatedExpiryDate,
                ExpirySource = before.ExpirySource,
                OpenedOn = before.OpenedOn,
                FrozenOn = before.FrozenOn,
                DefrostedOn = before.DefrostedOn,
                CurrentStorage = before.CurrentStorage,
                EstimatePending = before.EstimatePending,
                BeforeStateChange = before.Before,
            };

    /// <summary>A unit added in the app (#58): no scan, so no scan or device; the user who added it instead.</summary>
    public static Unit AddedInApp(Guid unitId, Guid productId, Guid userId, DateTimeOffset at, DateOnly? estimatedExpiry) =>
        new(unitId, productId, null, UnitStatus.InStock, null, null, at, null, null, null, estimatedExpiry, null,
            estimatedExpiry is null ? null : Stock.ExpirySource.ShelfLife, userId);

    /// <summary><see cref="CheckOutSource"/> for check-outs from the stock list (scanner check-outs use "scanner").</summary>
    public const string AppSource = "ui";

    public Unit CheckOut(Guid scanId, DateTimeOffset at, string source) =>
        this with { Status = UnitStatus.CheckedOut, CheckedOutAt = at, CheckOutScanId = scanId, CheckOutSource = source };

    /// <summary>Checked out from the stock list by a signed-in user (no scan). <paramref name="reason"/> is null for an
    /// ordinary check-out (used), or <see cref="CheckOutReasons.StockCheck"/> for "Gone" (#94).</summary>
    public Unit CheckOutByUser(Guid userId, DateTimeOffset at, string? reason = null) =>
        this with
        {
            Status = UnitStatus.CheckedOut, CheckedOutAt = at, CheckOutScanId = null, CheckOutSource = AppSource,
            CheckedOutBy = userId, CheckOutReason = reason,
        };

    /// <summary>Someone said it's still here (#94): it stops asking "Still got this?" for a while. The date is unchanged.</summary>
    public Unit ConfirmedStillHere(DateTimeOffset at, string via) => this with { LastConfirmedAt = at, LastConfirmedVia = via };

    /// <summary>Undo of "Still here" (#94): it asks again.</summary>
    public Unit WithoutConfirmation() => this with { LastConfirmedAt = null, LastConfirmedVia = null };

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
            CheckOutReason = null,
        };
}

/// <summary>Why a unit was checked out (#94). Missing means used (the ordinary case).</summary>
public static class CheckOutReasons
{
    /// <summary>"Gone" from a "Still got this?" prompt: a correction to the stock, not something used just now.</summary>
    public const string StockCheck = "stockCheck";
}

/// <summary>How a unit was last confirmed as still in the house (#94; #98 adds the stock check by place).</summary>
public static class ConfirmedVia
{
    public const string Prompt = "prompt";

    public const string StockCheck = "stockCheck";
}

/// <summary>A check-out scan with no unit in stock to remove (unknown barcode, or nothing in stock).</summary>
public sealed record UnmatchedCheckOut(
    Guid ScanId, string Barcode, Guid? ProductId, string DeviceId, DateTimeOffset ScannedAt, DateTimeOffset RecordedAt,
    DateTimeOffset? DismissedAt = null, Guid? DismissedBy = null)
{
    /// <summary>Dismissed on the scanners screen (#55): reviewed, nothing to do. Stays dismissed.</summary>
    public bool IsDismissed => DismissedAt is not null;
}
