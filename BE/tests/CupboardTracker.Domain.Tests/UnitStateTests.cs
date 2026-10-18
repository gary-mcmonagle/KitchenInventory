using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Domain.Tests;

/// <summary>Opened, frozen and defrosted (#65).</summary>
public sealed class UnitStateTests
{
    private static readonly DateOnly Bought = new(2026, 10, 1);
    private static readonly DateOnly Sealed = Bought.AddDays(180); // a jar: about 6 months sealed

    private static readonly ProductStateEstimates Estimates = new(
        new StateShelfLife(7, Storage.Fridge, 0.8, false, "jev"),
        new StateShelfLife(90, Storage.Freezer, 0.7, false, "jev"),
        new StateShelfLife(2, Storage.Fridge, 0.6, false, "jev"),
        new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));

    private static Unit Jar(DateOnly? expiry = null, ExpirySource? source = ExpirySource.ShelfLife) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "5000354922831", UnitStatus.InStock, null, null,
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), null, null, null, expiry ?? Sealed, null, source);

    private static Unit Applied(UnitStateChange change) => Assert.IsType<UnitStateChange.Applied>(change).Unit;

    [Fact]
    public void Opened_takes_the_open_date_plus_the_opened_shelf_life_and_where_to_keep_it()
    {
        var opened = Applied(UnitStates.Apply(Jar(), UnitState.Opened, new DateOnly(2026, 10, 5), Estimates));

        Assert.Equal((new DateOnly(2026, 10, 12), ExpirySource.Opened, Storage.Fridge, new DateOnly(2026, 10, 5)),
            (opened.EstimatedExpiryDate, opened.ExpirySource, opened.CurrentStorage, opened.OpenedOn));
        Assert.True(opened.IsOpened);
        Assert.False(opened.EstimatePending);
    }

    [Fact]
    public void Opening_never_makes_something_last_longer()
    {
        var nearlyGone = Jar(new DateOnly(2026, 10, 7));

        var opened = Applied(UnitStates.Apply(nearlyGone, UnitState.Opened, new DateOnly(2026, 10, 5), Estimates));

        Assert.Equal(new DateOnly(2026, 10, 7), opened.EstimatedExpiryDate);
    }

    [Fact]
    public void Opening_a_unit_with_no_estimate_yet_uses_the_opened_shelf_life()
    {
        var undated = Jar(expiry: null, source: null) with { EstimatedExpiryDate = null };

        Assert.Equal(new DateOnly(2026, 10, 12),
            Applied(UnitStates.Apply(undated, UnitState.Opened, new DateOnly(2026, 10, 5), Estimates)).EstimatedExpiryDate);
    }

    [Fact]
    public void Frozen_counts_from_the_freeze_date_and_defrosted_from_the_defrost_date()
    {
        var frozen = Applied(UnitStates.Apply(Jar(), UnitState.Frozen, new DateOnly(2026, 10, 3), Estimates));
        Assert.Equal((new DateOnly(2027, 1, 1), Storage.Freezer, ExpirySource.Frozen), (frozen.EstimatedExpiryDate, frozen.CurrentStorage, frozen.ExpirySource));
        Assert.True(frozen.IsFrozen);

        var defrosted = Applied(UnitStates.Apply(frozen, UnitState.Defrosted, new DateOnly(2026, 11, 20), Estimates));
        Assert.Equal((new DateOnly(2026, 11, 22), Storage.Fridge, ExpirySource.Defrosted), (defrosted.EstimatedExpiryDate, defrosted.CurrentStorage, defrosted.ExpirySource));
        Assert.False(defrosted.IsFrozen);
    }

    [Fact]
    public void Defrosted_only_from_frozen_and_nothing_once_checked_out()
    {
        Assert.Equal(UnitStateRefusal.NotFrozen,
            Assert.IsType<UnitStateChange.Refused>(UnitStates.Apply(Jar(), UnitState.Defrosted, Bought, Estimates)).Reason);
        var gone = Jar() with { Status = UnitStatus.CheckedOut };
        Assert.Equal(UnitStateRefusal.NotInStock,
            Assert.IsType<UnitStateChange.Refused>(UnitStates.Apply(gone, UnitState.Opened, Bought, Estimates)).Reason);
    }

    [Fact]
    public void Repeating_an_action_changes_nothing()
    {
        var opened = Applied(UnitStates.Apply(Jar(), UnitState.Opened, new DateOnly(2026, 10, 5), Estimates));

        var again = Assert.IsType<UnitStateChange.Applied>(UnitStates.Apply(opened, UnitState.Opened, new DateOnly(2026, 10, 9), Estimates));

        Assert.False(again.Changed);
        Assert.Same(opened, again.Unit);
        var frozen = Applied(UnitStates.Apply(Jar(), UnitState.Frozen, Bought, Estimates));
        Assert.False(Assert.IsType<UnitStateChange.Applied>(UnitStates.Apply(frozen, UnitState.Frozen, Bought, Estimates)).Changed);
        var defrosted = Applied(UnitStates.Apply(frozen, UnitState.Defrosted, Bought.AddDays(9), Estimates));
        Assert.False(Assert.IsType<UnitStateChange.Applied>(UnitStates.Apply(defrosted, UnitState.Defrosted, Bought.AddDays(10), Estimates)).Changed);
    }

    [Fact]
    public void The_latest_action_decides_when_opened_and_frozen_combine()
    {
        var opened = Applied(UnitStates.Apply(Jar(), UnitState.Opened, new DateOnly(2026, 10, 5), Estimates));

        var frozen = Applied(UnitStates.Apply(opened, UnitState.Frozen, new DateOnly(2026, 10, 6), Estimates));

        Assert.Equal((new DateOnly(2027, 1, 4), ExpirySource.Frozen), (frozen.EstimatedExpiryDate, frozen.ExpirySource));
        Assert.True(frozen.IsOpened && frozen.IsFrozen);
    }

    [Fact]
    public void A_state_change_replaces_a_manual_date_and_undo_restores_everything_exactly()
    {
        var manual = Jar(new DateOnly(2027, 2, 1), ExpirySource.Manual);

        var frozen = Applied(UnitStates.Apply(manual, UnitState.Frozen, new DateOnly(2026, 10, 3), Estimates));
        Assert.Equal(ExpirySource.Frozen, frozen.ExpirySource);

        Assert.Equal(manual, frozen.UndoStateChange());
    }

    [Fact]
    public void Without_a_product_estimate_the_state_is_recorded_pending_and_filled_in_later()
    {
        var pending = Applied(UnitStates.Apply(Jar(), UnitState.Opened, new DateOnly(2026, 10, 5), ProductStateEstimates.None));

        Assert.Equal((Sealed, ExpirySource.Opened, true, new DateOnly(2026, 10, 5)),
            (pending.EstimatedExpiryDate, pending.ExpirySource, pending.EstimatePending, pending.OpenedOn));
        Assert.Null(UnitStates.Estimate(pending, ProductStateEstimates.None));

        var filled = UnitStates.Estimate(pending, Estimates)!;
        Assert.Equal((new DateOnly(2026, 10, 12), false, Storage.Fridge), (filled.EstimatedExpiryDate, filled.EstimatePending, filled.CurrentStorage));
    }

    [Fact]
    public void Frozen_when_pending_is_still_in_the_freezer()
    {
        var pending = Applied(UnitStates.Apply(Jar(), UnitState.Frozen, Bought, ProductStateEstimates.None));

        Assert.Equal((Storage.Freezer, true), (pending.CurrentStorage, pending.EstimatePending));
    }

    [Fact]
    public void Check_out_takes_opened_units_first_and_units_in_the_freezer_last()
    {
        var soonestSealed = Jar(new DateOnly(2026, 10, 10));
        var opened = Jar(new DateOnly(2026, 10, 20)) with { OpenedOn = Bought };
        var frozenSoonest = Jar(new DateOnly(2026, 10, 2)) with { FrozenOn = Bought };
        var defrosted = Jar(new DateOnly(2026, 10, 15)) with { FrozenOn = Bought, DefrostedOn = Bought.AddDays(1) };

        Assert.Equal([opened, soonestSealed, defrosted, frozenSoonest],
            UnitSelection.InCheckOutOrder([frozenSoonest, defrosted, soonestSealed, opened]));
    }

    [Fact]
    public void A_manual_once_opened_survives_jevs_answers()
    {
        var states = ProductStateEstimates.None with { Opened = StateShelfLife.Manual(3, Storage.Fridge) };

        var fromJev = states.FromJev(Estimates.Opened!, Estimates.Frozen!, Estimates.Defrosted!, DateTimeOffset.UnixEpoch);

        Assert.Equal((3, true), (fromJev.Opened!.Days, fromJev.Opened.IsManual));
        Assert.Equal(90, fromJev.Frozen!.Days);
    }
}

public sealed class UnitStateUndoChainTests
{
    private static readonly ProductStateEstimates Estimates = new(
        new StateShelfLife(7, Storage.Fridge, 0.8, false, "jev"), new StateShelfLife(90, Storage.Freezer, 0.7, false, "jev"),
        new StateShelfLife(2, Storage.Fridge, 0.6, false, "jev"), DateTimeOffset.UnixEpoch);

    private static Unit Apply(Unit unit, UnitState state, DateOnly on) =>
        Assert.IsType<UnitStateChange.Applied>(UnitStates.Apply(unit, state, on, Estimates)).Unit;

    [Fact]
    public void Each_change_can_be_undone_in_turn_back_to_exactly_how_it_was()
    {
        var original = new Unit(Guid.NewGuid(), Guid.NewGuid(), null, UnitStatus.InStock, null, null, DateTimeOffset.UnixEpoch,
            null, null, null, new DateOnly(2026, 10, 31), null, ExpirySource.ShelfLife);
        var frozen = Apply(original, UnitState.Frozen, new DateOnly(2026, 10, 18));
        var defrosted = Apply(frozen, UnitState.Defrosted, new DateOnly(2026, 10, 20));

        var undoneOnce = defrosted.UndoStateChange();
        Assert.Equal(frozen, undoneOnce);
        Assert.Equal(original, undoneOnce.UndoStateChange());
    }

    [Fact]
    public void Only_the_last_five_changes_are_kept()
    {
        var unit = new Unit(Guid.NewGuid(), Guid.NewGuid(), null, UnitStatus.InStock, null, null, DateTimeOffset.UnixEpoch,
            null, null, null, new DateOnly(2026, 10, 31), null, ExpirySource.ShelfLife);
        for (var day = 1; day <= 8; day++)
        {
            unit = Apply(unit, day % 2 == 1 ? UnitState.Frozen : UnitState.Defrosted, new DateOnly(2026, 10, 10 + day));
        }

        var depth = 0;
        for (var s = unit.BeforeStateChange; s is not null; s = s.Before)
        {
            depth++;
        }

        Assert.Equal(UnitEstimateSnapshot.MaxDepth, depth);
    }
}
