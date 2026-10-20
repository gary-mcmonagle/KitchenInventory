using CupboardTracker.Application.Products;
using CupboardTracker.Application.Push;
using CupboardTracker.Application.RunOut;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

public abstract record CheckOutFromAppResult
{
    public sealed record CheckedOut(Guid UnitId, Guid ProductId, string DisplayName, int RemainingCount) : CheckOutFromAppResult;

    /// <summary>Nothing of that product in stock (409): nothing changed.</summary>
    public sealed record NothingInStock(Guid ProductId) : CheckOutFromAppResult;
}

/// <summary>
/// Check out one unit from the stock list (#50): the same unit a check-out scan would take (<see cref="UnitSelection"/>),
/// ETag-guarded so the app and a scanner never take the same unit, recorded as <c>checkOutSource: "ui"</c> with the user.
/// </summary>
public sealed partial class CheckOutFromApp(
    IProductRepository products,
    IUnitRepository units,
    TimeProvider clock,
    ILogger<CheckOutFromApp> logger,
    AddStockChangeToPush? pushes = null,
    AddWhenRunOut? runOut = null)
{
    private const int MaxRounds = 5;

    public async Task<CheckOutFromAppResult> ExecuteAsync(Guid productId, CurrentUser user, CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxRounds; round++)
        {
            var inStock = await units.GetInStockAsync(productId, cancellationToken);
            if (inStock.Count == 0)
            {
                return new CheckOutFromAppResult.NothingInStock(productId);
            }

            var byId = inStock.ToDictionary(v => v.Value.UnitId);
            foreach (var candidate in UnitSelection.InCheckOutOrder(inStock.Select(v => v.Value)))
            {
                var checkedOut = candidate.CheckOutByUser(user.ObjectId, clock.GetUtcNow());
                if (await units.TryReplaceAsync(byId[candidate.UnitId], checkedOut, cancellationToken))
                {
                    var product = await products.GetAsync(productId, cancellationToken);
                    var name = product?.DisplayName ?? "Unknown item";
                    LogCheckedOut(candidate.UnitId, productId, user.ObjectId);
                    var at = checkedOut.CheckedOutAt ?? clock.GetUtcNow();
                    await PushAsync(pushes, candidate.UnitId, productId, name, user, at, cancellationToken);
                    await RunOutAsync(runOut, candidate.UnitId, productId, at, cancellationToken);
                    return new CheckOutFromAppResult.CheckedOut(candidate.UnitId, productId, name, inStock.Count - 1);
                }

                LogConflict(candidate.UnitId); // taken by a scanner or another tap meanwhile: try the next one
            }
        }

        throw new InvalidOperationException($"Couldn't check out a unit of {productId} after {MaxRounds} rounds of conflicts.");
    }

    /// <summary>An app check-out joins the stock change push (#81); the person who did it isn't told.</summary>
    internal static async Task PushAsync(
        AddStockChangeToPush? pushes, Guid unitId, Guid productId, string name, CurrentUser user, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (pushes is not null)
        {
            await pushes.ExecuteAsync(
                new StockChange(StockChangeKeys.ForApp(unitId, "out", at), StockChangeKind.Removed, name, at, productId, user.ObjectId, user.DisplayName),
                cancellationToken);
        }
    }

    /// <summary>Every check-out may put the generic name on the shopping list (#89).</summary>
    internal static Task RunOutAsync(AddWhenRunOut? runOut, Guid unitId, Guid productId, DateTimeOffset at, CancellationToken cancellationToken) =>
        runOut?.CheckedOutAsync(unitId, productId, at, cancellationToken) ?? Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Checked out unit {UnitId} of product {ProductId} from the app (user {UserId}).")]
    private partial void LogCheckedOut(Guid unitId, Guid productId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unit {UnitId} changed under an app check-out; trying the next unit.")]
    private partial void LogConflict(Guid unitId);
}
