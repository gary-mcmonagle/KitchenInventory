using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging.Abstractions;

namespace CupboardTracker.Application.Tests.Stock;

public sealed class CheckOutFromAppTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly Guid gravy = Guid.NewGuid();

    public CheckOutFromAppTests() =>
        products.Products[gravy] = Product.Found(gravy, "5000354922831", new ProductDetails("Beef Gravy", "Bisto", "230g", null, []), Now, 1);

    private CheckOutFromApp CheckOut() => new(products, units, new FixedClock(Now), NullLogger<CheckOutFromApp>.Instance);

    private UndoCheckOut Undo() => new(units, NullLogger<UndoCheckOut>.Instance);

    private Unit AddUnit(int daysAgo, DateOnly? expiry = null)
    {
        var unit = new Unit(Guid.NewGuid(), gravy, "5000354922831", UnitStatus.InStock, Guid.NewGuid(), "scanner-in",
            Now.AddDays(-daysAgo), null, null, null, expiry);
        units.Add(unit);
        return unit;
    }

    [Fact]
    public async Task Takes_the_unit_a_scanner_would_and_records_the_app_and_user()
    {
        AddUnit(daysAgo: 1);
        var oldest = AddUnit(daysAgo: 5);
        AddUnit(daysAgo: 3);

        var result = Assert.IsType<CheckOutFromAppResult.CheckedOut>(await CheckOut().ExecuteAsync(gravy, Gary, CancellationToken.None));

        Assert.Equal((oldest.UnitId, gravy, "Beef Gravy", 2), (result.UnitId, result.ProductId, result.DisplayName, result.RemainingCount));
        var stored = units.Units[oldest.UnitId].Unit;
        Assert.Equal(UnitStatus.CheckedOut, stored.Status);
        Assert.Equal(Now, stored.CheckedOutAt);
        Assert.Equal("ui", stored.CheckOutSource);
        Assert.Equal(Gary.ObjectId, stored.CheckedOutBy);
        Assert.Null(stored.CheckOutScanId);
    }

    [Fact]
    public async Task Soonest_expiry_first_like_the_scanner()
    {
        AddUnit(daysAgo: 9);
        var expiresSoon = AddUnit(daysAgo: 1, expiry: new DateOnly(2026, 10, 20));

        var result = Assert.IsType<CheckOutFromAppResult.CheckedOut>(await CheckOut().ExecuteAsync(gravy, Gary, CancellationToken.None));

        Assert.Equal(expiresSoon.UnitId, result.UnitId);
    }

    [Fact]
    public async Task Nothing_in_stock_changes_nothing()
    {
        var result = await CheckOut().ExecuteAsync(gravy, Gary, CancellationToken.None);

        Assert.IsType<CheckOutFromAppResult.NothingInStock>(result);
        Assert.Empty(units.Units);
    }

    [Fact]
    public async Task A_scanner_taking_the_same_unit_at_the_same_moment_makes_the_app_take_the_next_one()
    {
        var first = AddUnit(daysAgo: 5);
        var second = AddUnit(daysAgo: 3);
        units.StolenBeforeReplace.Add(first.UnitId); // a check-out scan takes it just before our write

        var result = Assert.IsType<CheckOutFromAppResult.CheckedOut>(await CheckOut().ExecuteAsync(gravy, Gary, CancellationToken.None));

        Assert.Equal(second.UnitId, result.UnitId);
        Assert.Equal("scanner", units.Units[first.UnitId].Unit.CheckOutSource);
        Assert.Equal("ui", units.Units[second.UnitId].Unit.CheckOutSource);
        Assert.Empty(units.InStock(gravy));
    }

    [Fact]
    public async Task Undo_restores_that_unit_and_clears_the_check_out()
    {
        AddUnit(daysAgo: 1);
        var oldest = AddUnit(daysAgo: 5);
        await CheckOut().ExecuteAsync(gravy, Gary, CancellationToken.None);

        var result = Assert.IsType<UndoCheckOutResult.InStock>(await Undo().ExecuteAsync(oldest.UnitId, CancellationToken.None));

        Assert.True(result.WasRestored);
        Assert.Equal(2, result.Count);
        Assert.Equal(oldest, units.Units[oldest.UnitId].Unit); // exactly as before the check-out
    }

    [Fact]
    public async Task Undo_is_idempotent()
    {
        var unit = AddUnit(daysAgo: 1);

        var result = Assert.IsType<UndoCheckOutResult.InStock>(await Undo().ExecuteAsync(unit.UnitId, CancellationToken.None));

        Assert.False(result.WasRestored);
        Assert.Equal(1, result.Count);
        Assert.Equal(1, units.Units[unit.UnitId].Version); // not written
    }

    [Fact]
    public async Task Undo_an_unknown_unit()
    {
        Assert.IsType<UndoCheckOutResult.NotFound>(await Undo().ExecuteAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
