using System.Globalization;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

public abstract record ChangeUnitStateResult
{
    /// <summary>The unit now; a repeat of an action already applied changes nothing and is still Changed.</summary>
    public sealed record Changed(Guid ProductId, ProductUnitView Unit) : ChangeUnitStateResult;

    public sealed record Invalid(string Error) : ChangeUnitStateResult;

    public sealed record NotFound(Guid UnitId) : ChangeUnitStateResult;

    /// <summary>409: not in stock, Defrosted on a unit that isn't frozen, nothing to undo, or changed while saving.</summary>
    public sealed record Conflict(Guid UnitId, string Reason) : ChangeUnitStateResult;
}

/// <summary>
/// Opened, Froze it and Defrosted on a unit in item detail (#65): records the state on <paramref name="on"/> (default
/// today, UK) and re-estimates the unit from its product's state estimates, at once and without calling Jev (pending
/// until the timer has them). Idempotent; the estimate before is kept for <see cref="UndoUnitStateChange"/>.
/// </summary>
public sealed partial class ChangeUnitState(
    IUnitRepository units, IProductRepository products, StockSettings settings, TimeProvider clock,
    ILogger<ChangeUnitState> logger)
{
    public async Task<ChangeUnitStateResult> ExecuteAsync(
        Guid unitId, UnitState state, string? date, CurrentUser user, CancellationToken cancellationToken)
    {
        var today = settings.Today(clock.GetUtcNow());
        DateOnly on = today;
        if (!string.IsNullOrEmpty(date)
            && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out on))
        {
            return new ChangeUnitStateResult.Invalid("Date must be a real date, YYYY-MM-DD.");
        }

        if (on > today)
        {
            return new ChangeUnitStateResult.Invalid("That date is in the future.");
        }

        var current = await units.FindAsync(unitId, cancellationToken);
        if (current is null)
        {
            return new ChangeUnitStateResult.NotFound(unitId);
        }

        if (on < EstimatedExpiry.BoughtDate(current.Value.CheckedInAt, settings.HomeTimeZone))
        {
            return new ChangeUnitStateResult.Invalid("That's before it was bought.");
        }

        var product = await products.GetAsync(current.Value.ProductId, cancellationToken);
        switch (UnitStates.Apply(current.Value, state, on, product?.States ?? ProductStateEstimates.None))
        {
            case UnitStateChange.Refused { Reason: UnitStateRefusal.NotFrozen }:
                return new ChangeUnitStateResult.Conflict(unitId, "That unit isn't frozen.");
            case UnitStateChange.Refused:
                return new ChangeUnitStateResult.Conflict(unitId, "That unit isn't in stock any more.");
            case UnitStateChange.Applied { Changed: false, Unit: var same }:
                return new ChangeUnitStateResult.Changed(same.ProductId, ProductUnitView.From(same, today));
            case UnitStateChange.Applied { Unit: var updated }:
                if (!await units.TryReplaceAsync(current, updated, cancellationToken))
                {
                    return new ChangeUnitStateResult.Conflict(unitId, "That unit changed while saving. Try again.");
                }

                LogChanged(unitId, state, on, updated.EstimatedExpiryDate, updated.EstimatePending, user.ObjectId);
                return new ChangeUnitStateResult.Changed(updated.ProductId, ProductUnitView.From(updated, today));
            default:
                throw new InvalidOperationException("Unexpected state change.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Unit {UnitId} marked {State} on {On} by user {UserId}: estimated expiry {Expiry}, pending {Pending}.")]
    private partial void LogChanged(Guid unitId, UnitState state, DateOnly on, DateOnly? expiry, bool pending, Guid userId);
}

/// <summary>Undo for <see cref="ChangeUnitState"/> (#65): the unit's estimate fields exactly as before; one level.</summary>
public sealed partial class UndoUnitStateChange(
    IUnitRepository units, StockSettings settings, TimeProvider clock, ILogger<UndoUnitStateChange> logger)
{
    public async Task<ChangeUnitStateResult> ExecuteAsync(Guid unitId, CurrentUser user, CancellationToken cancellationToken)
    {
        var current = await units.FindAsync(unitId, cancellationToken);
        if (current is null)
        {
            return new ChangeUnitStateResult.NotFound(unitId);
        }

        if (current.Value.Status != UnitStatus.InStock)
        {
            return new ChangeUnitStateResult.Conflict(unitId, "That unit isn't in stock any more.");
        }

        if (current.Value.BeforeStateChange is null)
        {
            return new ChangeUnitStateResult.Conflict(unitId, "There's nothing to undo.");
        }

        var restored = current.Value.UndoStateChange();
        if (!await units.TryReplaceAsync(current, restored, cancellationToken))
        {
            return new ChangeUnitStateResult.Conflict(unitId, "That unit changed while saving. Try again.");
        }

        LogUndone(unitId, user.ObjectId);
        return new ChangeUnitStateResult.Changed(restored.ProductId, ProductUnitView.From(restored, settings.Today(clock.GetUtcNow())));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Unit {UnitId}'s last state change undone by user {UserId}.")]
    private partial void LogUndone(Guid unitId, Guid userId);
}

/// <summary>
/// "Opened" on a stock list row (#65, Gary 2026-10-18): opens the unit that would be used next (check-out order,
/// skipping units in the freezer), today. Refused when one is already open (the button is greyed out then) or every
/// unit is in the freezer.
/// </summary>
public sealed class OpenNextUnit(IUnitRepository units, ChangeUnitState change)
{
    public async Task<ChangeUnitStateResult> ExecuteAsync(Guid productId, CurrentUser user, CancellationToken cancellationToken)
    {
        var inStock = (await units.GetInStockAsync(productId, cancellationToken)).Select(v => v.Value).ToList();
        if (inStock.Count == 0)
        {
            return new ChangeUnitStateResult.NotFound(productId);
        }

        if (inStock.Any(u => u.IsOpened))
        {
            return new ChangeUnitStateResult.Conflict(productId, "One's already open.");
        }

        var next = UnitSelection.InCheckOutOrder(inStock).FirstOrDefault(u => !u.IsFrozen);
        return next is null
            ? new ChangeUnitStateResult.Conflict(productId, "They're all in the freezer.")
            : await change.ExecuteAsync(next.UnitId, UnitState.Opened, null, user, cancellationToken);
    }
}
