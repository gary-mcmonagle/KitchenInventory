namespace CupboardTracker.Domain.Products;

/// <summary>
/// How long one state keeps, and where to keep it then (#65): opened, frozen or defrosted. Same shape as a shelf life.
/// </summary>
/// <param name="Days">Days from the day the state started (opened, frozen or defrosted).</param>
/// <param name="Storage">Where to keep it in that state (always the freezer when frozen).</param>
/// <param name="Source"><see cref="ShelfLife.JevSource"/>, or <see cref="ShelfLife.ManualSource"/> when set by hand
/// (only "once opened" can be, in item detail); Jev never overwrites a manual one.</param>
public sealed record StateShelfLife(int Days, Storage Storage, double Confidence, bool LowConfidence, string Source)
{
    public bool IsManual => Source == ShelfLife.ManualSource;

    public static StateShelfLife Manual(int days, Storage storage) => new(days, storage, 1, false, ShelfLife.ManualSource);
}

/// <summary>
/// A product's estimates for when a unit is opened, frozen or defrosted (#65), asked of Jev once per product after its
/// shelf life. <see cref="EstimatedAt"/> is null until Jev has answered (a manual "once opened" can come first).
/// </summary>
public sealed record ProductStateEstimates(
    StateShelfLife? Opened, StateShelfLife? Frozen, StateShelfLife? Defrosted, DateTimeOffset? EstimatedAt)
{
    public static readonly ProductStateEstimates None = new(null, null, null, null);

    public StateShelfLife? For(UnitState state) => state switch
    {
        UnitState.Opened => Opened,
        UnitState.Frozen => Frozen,
        UnitState.Defrosted => Defrosted,
        _ => null,
    };

    /// <summary>Jev's answers, keeping a manual "once opened".</summary>
    public ProductStateEstimates FromJev(StateShelfLife opened, StateShelfLife frozen, StateShelfLife defrosted, DateTimeOffset at) =>
        new(Opened is { IsManual: true } ? Opened : opened, frozen, defrosted, at);
}

/// <summary>What happened to a unit after it was bought (#65).</summary>
public enum UnitState
{
    Opened,
    Frozen,
    Defrosted,
}
