namespace CupboardTracker.Domain.Products;

/// <summary>Where a product is normally kept at home before it's opened (asked of Jev with the shelf life, #52).</summary>
public enum Storage
{
    Cupboard,
    Fridge,
    Freezer,
}

/// <summary>
/// How long a product usually keeps after it's bought, unopened and stored the usual way (#52, spike #40). Estimated
/// once per product; a unit's estimated expiry date is its bought date plus <see cref="Days"/>.
/// </summary>
/// <param name="Days">The estimate, in days.</param>
/// <param name="UsualStorage">Where it's normally kept; the estimate is for that storage.</param>
/// <param name="Confidence">The estimator's confidence, 0–1.</param>
/// <param name="LowConfidence">True when <see cref="Confidence"/> was below the threshold, so a cautious level was used.</param>
/// <param name="Source">Who estimated it: <see cref="JevSource"/> (manual estimates come with item detail, #53).</param>
/// <param name="EstimatedAt">When it was estimated.</param>
public sealed record ShelfLife(
    int Days, Storage UsualStorage, double Confidence, bool LowConfidence, string Source, DateTimeOffset EstimatedAt)
{
    public const string JevSource = "jev";

    /// <summary>Set by a person in item detail (#53). Jev never overwrites it.</summary>
    public const string ManualSource = "manual";

    public bool IsManual => Source == ManualSource;

    public static ShelfLife Manual(int days, Storage usualStorage, DateTimeOffset at) =>
        new(days, usualStorage, 1, false, ManualSource, at);
}
