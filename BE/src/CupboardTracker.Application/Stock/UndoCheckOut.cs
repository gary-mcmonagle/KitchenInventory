using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

public abstract record UndoCheckOutResult
{
    /// <summary>The unit is in stock (just restored, or it already was: undo is idempotent).</summary>
    public sealed record InStock(Guid UnitId, Guid ProductId, int Count, bool WasRestored) : UndoCheckOutResult;

    public sealed record NotFound(Guid UnitId) : UndoCheckOutResult;
}

/// <summary>
/// Undo a check-out (#50): the unit goes back to <c>inStock</c> with its check-out fields cleared. Undoing a unit that's
/// already in stock changes nothing. ETag-guarded like check-out.
/// </summary>
public sealed partial class UndoCheckOut(IUnitRepository units, ILogger<UndoCheckOut> logger)
{
    private const int MaxAttempts = 5;

    public async Task<UndoCheckOutResult> ExecuteAsync(Guid unitId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await units.FindAsync(unitId, cancellationToken);
            if (current is null)
            {
                return new UndoCheckOutResult.NotFound(unitId);
            }

            var unit = current.Value;
            if (unit.Status == UnitStatus.InStock)
            {
                return new UndoCheckOutResult.InStock(unitId, unit.ProductId, await CountAsync(unit.ProductId, cancellationToken), false);
            }

            if (await units.TryReplaceAsync(current, unit.UndoCheckOut(), cancellationToken))
            {
                LogRestored(unitId, unit.ProductId, unit.CheckOutSource);
                return new UndoCheckOutResult.InStock(unitId, unit.ProductId, await CountAsync(unit.ProductId, cancellationToken), true);
            }
        }

        throw new InvalidOperationException($"Unit {unitId} kept changing; couldn't undo its check-out.");
    }

    private async Task<int> CountAsync(Guid productId, CancellationToken cancellationToken) =>
        (await units.GetInStockAsync(productId, cancellationToken)).Count;

    [LoggerMessage(Level = LogLevel.Information, Message = "Undid the check-out of unit {UnitId} (product {ProductId}, was checked out via {Source}).")]
    private partial void LogRestored(Guid unitId, Guid productId, string? source);
}
