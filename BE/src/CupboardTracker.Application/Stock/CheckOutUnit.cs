using CupboardTracker.Application.Products;
using CupboardTracker.Application.Push;
using CupboardTracker.Application.RunOut;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

public abstract record CheckOutUnitResult
{
    public sealed record CheckedOut(Guid UnitId, Guid ProductId, string DisplayName, int RemainingCount) : CheckOutUnitResult;

    public sealed record NotFound(Guid UnitId) : CheckOutUnitResult;

    /// <summary>409: already checked out (or taken by a scanner meanwhile): nothing changed.</summary>
    public sealed record NotInStock(Guid UnitId) : CheckOutUnitResult;
}

/// <summary>
/// Checks out one particular unit from item detail (#53), recorded like a stock-list check-out
/// (<c>checkOutSource: "ui"</c>, <c>checkedOutBy</c>). ETag-guarded, so a scanner can't take the same unit. Undo uses
/// <see cref="UndoCheckOut"/>.
/// </summary>
public sealed partial class CheckOutUnit(
    IProductRepository products, IUnitRepository units, TimeProvider clock, ILogger<CheckOutUnit> logger,
    AddStockChangeToPush? pushes = null, AddWhenRunOut? runOut = null)
{
    /// <param name="reason">Null for an ordinary check-out; <see cref="CheckOutReasons.StockCheck"/> for "Gone" (#94),
    /// which counts as running out (#89) but pushes to nobody (#81): it's a correction, not news.</param>
    public async Task<CheckOutUnitResult> ExecuteAsync(Guid unitId, CurrentUser user, CancellationToken cancellationToken, string? reason = null)
    {
        var current = await units.FindAsync(unitId, cancellationToken);
        if (current is null)
        {
            return new CheckOutUnitResult.NotFound(unitId);
        }

        var now = clock.GetUtcNow();
        if (current.Value.Status != UnitStatus.InStock
            || !await units.TryReplaceAsync(current, current.Value.CheckOutByUser(user.ObjectId, now, reason), cancellationToken))
        {
            return new CheckOutUnitResult.NotInStock(unitId);
        }

        var productId = current.Value.ProductId;
        var remaining = (await units.GetInStockAsync(productId, cancellationToken)).Count;
        var name = (await products.GetAsync(productId, cancellationToken))?.DisplayName ?? "Unknown item";
        LogCheckedOut(unitId, productId, user.ObjectId, reason ?? "used");
        if (reason != CheckOutReasons.StockCheck)
        {
            await CheckOutFromApp.PushAsync(pushes, unitId, productId, name, user, now, cancellationToken);
        }

        await CheckOutFromApp.RunOutAsync(runOut, unitId, productId, now, cancellationToken);
        return new CheckOutUnitResult.CheckedOut(unitId, productId, name, remaining);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Checked out unit {UnitId} of product {ProductId} from the app (user {UserId}, reason {Reason}).")]
    private partial void LogCheckedOut(Guid unitId, Guid productId, Guid userId, string reason);
}
