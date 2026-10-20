using CupboardTracker.Application.Products;
using CupboardTracker.Application.Push;
using CupboardTracker.Application.RunOut;
using CupboardTracker.Application.Users;
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
/// already in stock changes nothing. ETag-guarded like check-out. A restore by a signed-in person joins the stock change
/// push as "added back" (#81), so a check-out undone straight away nets out.
/// </summary>
public sealed partial class UndoCheckOut(
    IUnitRepository units, ILogger<UndoCheckOut> logger, IProductRepository? products = null, AddStockChangeToPush? pushes = null,
    TimeProvider? clock = null, AddWhenRunOut? runOut = null)
{
    private const int MaxAttempts = 5;

    public async Task<UndoCheckOutResult> ExecuteAsync(Guid unitId, CancellationToken cancellationToken, CurrentUser? user = null)
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
                if (runOut is not null && unit.CheckedOutAt is { } checkedOutAt)
                {
                    await runOut.UndoneAsync(unitId, checkedOutAt, cancellationToken); // an automatic add for it comes off the list (#89)
                }

                if (pushes is not null && user is not null)
                {
                    var at = (clock ?? TimeProvider.System).GetUtcNow();
                    var name = (products is null ? null : await products.GetAsync(unit.ProductId, cancellationToken))?.DisplayName ?? "Unknown item";
                    await pushes.ExecuteAsync(
                        new StockChange(StockChangeKeys.ForApp(unitId, "undo", at), StockChangeKind.Added, name, at, unit.ProductId, user.ObjectId, user.DisplayName),
                        cancellationToken);
                }

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
