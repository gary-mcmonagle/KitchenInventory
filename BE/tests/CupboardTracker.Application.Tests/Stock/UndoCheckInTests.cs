using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging.Abstractions;

namespace CupboardTracker.Application.Tests.Stock;

public sealed class UndoCheckInTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 18, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FixedClock clock = new(Now);
    private readonly Guid gravy = Guid.NewGuid();

    public UndoCheckInTests() =>
        products.Products[gravy] = Product.Found(gravy, "5000354922831", new ProductDetails("Beef Gravy", "Bisto", "230g", null, []), Now, 1);

    private UndoCheckIn Undo() => new(units, products, clock, NullLogger<UndoCheckIn>.Instance);

    private Unit AddUnit(string device, DateTimeOffset checkedInAt)
    {
        var unit = new Unit(Guid.NewGuid(), gravy, "5000354922831", UnitStatus.InStock, Guid.NewGuid(), device, checkedInAt, null, null, null, null);
        units.Add(unit);
        return unit;
    }

    [Fact]
    public async Task Takes_a_camera_check_in_out_of_stock_without_counting_it_as_used()
    {
        var unit = AddUnit(CameraScanning.Device, Now.AddMinutes(-2));

        var result = Assert.IsType<UndoCheckInResult.Undone>(await Undo().ExecuteAsync(unit.UnitId, Gary, CancellationToken.None));

        Assert.True(result.WasUndone);
        var stored = units.Units[unit.UnitId].Unit;
        Assert.Equal(UnitStatus.CheckedOut, stored.Status);
        Assert.Equal(CheckOutReasons.UndoneCheckIn, stored.CheckOutReason);
        Assert.Equal(Gary.ObjectId, stored.CheckedOutBy);
        Assert.Empty(units.InStock(gravy));
    }

    [Fact]
    public async Task Is_idempotent()
    {
        var unit = AddUnit(CameraScanning.Device, Now.AddMinutes(-2));
        await Undo().ExecuteAsync(unit.UnitId, Gary, CancellationToken.None);

        var again = Assert.IsType<UndoCheckInResult.Undone>(await Undo().ExecuteAsync(unit.UnitId, Gary, CancellationToken.None));

        Assert.False(again.WasUndone);
    }

    [Fact]
    public async Task Cannot_remove_ordinary_stock_or_an_old_check_in_or_one_checked_out_for_real()
    {
        var scanner = AddUnit("scanner-in", Now.AddMinutes(-2));
        var old = AddUnit(CameraScanning.Device, Now - UndoCheckIn.Window - TimeSpan.FromMinutes(1));
        var used = AddUnit(CameraScanning.Device, Now.AddHours(-1));
        units.Units[used.UnitId] = (used.CheckOutByUser(Gary.ObjectId, Now.AddMinutes(-5)), 2);

        foreach (var id in new[] { scanner.UnitId, old.UnitId, used.UnitId })
        {
            Assert.IsType<UndoCheckInResult.NotUndoable>(await Undo().ExecuteAsync(id, Gary, CancellationToken.None));
        }

        Assert.Equal(UnitStatus.InStock, units.Units[scanner.UnitId].Unit.Status);
        Assert.Equal(UnitStatus.InStock, units.Units[old.UnitId].Unit.Status);
        Assert.Null(units.Units[used.UnitId].Unit.CheckOutReason);
    }

    [Fact]
    public async Task An_unknown_unit_is_not_found()
    {
        Assert.IsType<UndoCheckInResult.NotFound>(await Undo().ExecuteAsync(Guid.NewGuid(), Gary, CancellationToken.None));
    }
}
