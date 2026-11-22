using CupboardTracker.Application.Push;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

public abstract record UndoCheckInResult
{
    /// <summary>It is out of stock now (just undone, or it already was).</summary>
    public sealed record Undone(Guid UnitId, Guid ProductId, bool WasUndone) : UndoCheckInResult;

    public sealed record NotFound(Guid UnitId) : UndoCheckInResult;

    /// <summary>409: not a check-in from the phone camera, or it's been in stock too long to be a mistake, or it has been
    /// checked out for real since. Nothing changed.</summary>
    public sealed record NotUndoable(string Reason) : UndoCheckInResult;
}

/// <summary>
/// Takes back a check-in from the phone camera (#133): the unit goes out of stock with the reason
/// <see cref="CheckOutReasons.UndoneCheckIn"/>, so it isn't counted as used. Only a unit the camera checked in, and only for
/// <see cref="Window"/> after, so it can't be used to remove ordinary stock. Idempotent. Joins the stock change push as a
/// removal, so it nets out with the check-in's "added".
/// </summary>
public sealed partial class UndoCheckIn(
    IUnitRepository units, IProductRepository products, TimeProvider clock, ILogger<UndoCheckIn> logger, AddStockChangeToPush? pushes = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private const int MaxAttempts = 5;

    public async Task<UndoCheckInResult> ExecuteAsync(Guid unitId, CurrentUser user, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await units.FindAsync(unitId, cancellationToken);
            if (current is null)
            {
                return new UndoCheckInResult.NotFound(unitId);
            }

            var unit = current.Value;
            if (unit.CheckInDeviceId != CameraScanning.Device)
            {
                return new UndoCheckInResult.NotUndoable("That unit wasn't checked in with the camera.");
            }

            if (unit.Status == UnitStatus.CheckedOut)
            {
                return unit.CheckOutReason == CheckOutReasons.UndoneCheckIn
                    ? new UndoCheckInResult.Undone(unitId, unit.ProductId, false)
                    : new UndoCheckInResult.NotUndoable("That unit has been checked out since.");
            }

            var now = clock.GetUtcNow();
            if (now - unit.CheckedInAt > Window)
            {
                return new UndoCheckInResult.NotUndoable("That check-in is too old to undo.");
            }

            if (await units.TryReplaceAsync(current, unit.UndoCheckIn(user.ObjectId, now), cancellationToken))
            {
                LogUndone(unit.ProductId);
                if (pushes is not null)
                {
                    var name = (await products.GetAsync(unit.ProductId, cancellationToken))?.DisplayName ?? "Unknown item";
                    await pushes.ExecuteAsync(
                        new StockChange(StockChangeKeys.ForApp(unitId, "undoCheckIn", now), StockChangeKind.Removed, name, now,
                            unit.ProductId, user.ObjectId, user.DisplayName),
                        cancellationToken);
                }

                return new UndoCheckInResult.Undone(unitId, unit.ProductId, true);
            }
        }

        throw new InvalidOperationException($"Unit {unitId} kept changing; couldn't undo its check-in.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Undid a camera check-in (product {ProductId}).")]
    private partial void LogUndone(Guid productId);
}
