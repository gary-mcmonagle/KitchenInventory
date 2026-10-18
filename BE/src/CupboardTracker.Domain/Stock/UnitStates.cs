using CupboardTracker.Domain.Products;

namespace CupboardTracker.Domain.Stock;

/// <summary>Why a state change was refused (#65).</summary>
public enum UnitStateRefusal
{
    NotInStock,

    /// <summary>Defrosted on a unit that isn't in the freezer.</summary>
    NotFrozen,
}

/// <summary>The outcome of marking a unit opened, frozen or defrosted.</summary>
public abstract record UnitStateChange
{
    /// <summary>The unit after the change. <see cref="Changed"/> is false for a repeat (idempotent).</summary>
    public sealed record Applied(Unit Unit, bool Changed) : UnitStateChange;

    public sealed record Refused(UnitStateRefusal Reason) : UnitStateChange;
}

/// <summary>
/// Opened, frozen and defrosted (#65): a unit's state changes its estimated expiry date and where to keep it, from the
/// product's state estimates. The latest action decides the estimate. A change replaces a manual date; the estimate
/// fields before it are kept for Undo. With no product estimate yet, the state is recorded, the date kept, and
/// <see cref="Unit.EstimatePending"/> set for the shelf-life timer to fill in.
/// </summary>
public static class UnitStates
{
    public static UnitStateChange Apply(Unit unit, UnitState state, DateOnly on, ProductStateEstimates estimates)
    {
        if (unit.Status != UnitStatus.InStock)
        {
            return new UnitStateChange.Refused(UnitStateRefusal.NotInStock);
        }

        var repeat = state switch
        {
            UnitState.Opened => unit.IsOpened,
            UnitState.Frozen => unit.IsFrozen,
            _ => unit.DefrostedOn is not null && !unit.IsFrozen,
        };
        if (repeat)
        {
            return new UnitStateChange.Applied(unit, false);
        }

        if (state == UnitState.Defrosted && !unit.IsFrozen)
        {
            return new UnitStateChange.Refused(UnitStateRefusal.NotFrozen);
        }

        var marked = (unit with { BeforeStateChange = unit.Snapshot() }) switch
        {
            var u when state == UnitState.Opened => u with { OpenedOn = on },
            var u when state == UnitState.Frozen => u with { FrozenOn = on, DefrostedOn = null },
            var u => u with { DefrostedOn = on },
        };
        var source = state switch
        {
            UnitState.Opened => ExpirySource.Opened,
            UnitState.Frozen => ExpirySource.Frozen,
            _ => ExpirySource.Defrosted,
        };
        marked = marked with { ExpirySource = source, CurrentStorage = state == UnitState.Frozen ? Storage.Freezer : marked.CurrentStorage };
        return new UnitStateChange.Applied(Estimate(marked, estimates) ?? marked with { EstimatePending = true }, true);
    }

    /// <summary>
    /// The unit with its date and storage worked out for its latest state (its <see cref="Unit.ExpirySource"/>), or
    /// null when the product has no estimate for that state yet. Clears <see cref="Unit.EstimatePending"/>.
    /// </summary>
    public static Unit? Estimate(Unit unit, ProductStateEstimates estimates)
    {
        var (state, since) = unit.ExpirySource switch
        {
            ExpirySource.Opened => (UnitState.Opened, unit.OpenedOn),
            ExpirySource.Frozen => (UnitState.Frozen, unit.FrozenOn),
            ExpirySource.Defrosted => (UnitState.Defrosted, unit.DefrostedOn),
            _ => (default(UnitState?), (DateOnly?)null),
        };
        if (state is null || since is null || estimates.For(state.Value) is not { } shelfLife)
        {
            return null;
        }

        var date = since.Value.AddDays(shelfLife.Days);
        if (state == UnitState.Opened && unit.BeforeStateChange?.EstimatedExpiryDate is { } before && before < date)
        {
            date = before; // opening never makes something last longer
        }

        return unit with
        {
            EstimatedExpiryDate = date,
            CurrentStorage = state == UnitState.Frozen ? Storage.Freezer : shelfLife.Storage,
            EstimatePending = false,
        };
    }
}
