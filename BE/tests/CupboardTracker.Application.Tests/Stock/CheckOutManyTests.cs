using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CupboardTracker.Application.Tests.Stock;

public sealed class CheckOutManyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 18, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly ListLogger<CheckOutMany> log = new();
    private readonly Guid gravy = Guid.NewGuid();

    public CheckOutManyTests() =>
        products.Products[gravy] = Product.Found(gravy, "5000354922831", new ProductDetails("Beef Gravy", "Bisto", "230g", null, []), Now, 1);

    private CheckOutMany ClearOut() =>
        new(new CheckOutUnit(products, units, new FixedClock(Now), NullLogger<CheckOutUnit>.Instance), log);

    private Unit AddUnit(UnitStatus status = UnitStatus.InStock)
    {
        var unit = new Unit(Guid.NewGuid(), gravy, "5000354922831", UnitStatus.InStock, Guid.NewGuid(), "scanner-in",
            Now.AddDays(-3), null, null, null, null);
        units.Add(unit);
        if (status == UnitStatus.CheckedOut)
        {
            units.Units[unit.UnitId] = (unit.CheckOut(Guid.NewGuid(), Now.AddDays(-1), "scanner"), 2);
        }

        return unit;
    }

    private async Task<CheckOutManyResult.Done> Run(params Guid[] ids) =>
        Assert.IsType<CheckOutManyResult.Done>(await ClearOut().ExecuteAsync(ids, Gary, CancellationToken.None));

    [Fact]
    public async Task Checks_out_every_listed_unit_like_a_single_app_check_out()
    {
        var a = AddUnit();
        var b = AddUnit();
        var untouched = AddUnit();

        var done = await Run(a.UnitId, b.UnitId);

        Assert.Equal((2, 0, 0), (done.CheckedOut, done.Unchanged, done.Failed));
        Assert.All(done.Units, u => Assert.Equal(gravy, u.ProductId));
        foreach (var id in new[] { a.UnitId, b.UnitId })
        {
            var stored = units.Units[id].Unit;
            Assert.Equal(UnitStatus.CheckedOut, stored.Status);
            Assert.Equal(Now, stored.CheckedOutAt);
            Assert.Equal("ui", stored.CheckOutSource);
            Assert.Equal(Gary.ObjectId, stored.CheckedOutBy);
        }

        Assert.Equal(UnitStatus.InStock, units.Units[untouched.UnitId].Unit.Status);
    }

    [Fact]
    public async Task A_unit_already_checked_out_or_unknown_is_not_an_error_and_the_others_are_still_done()
    {
        var gone = AddUnit(UnitStatus.CheckedOut);
        var here = AddUnit();
        var unknown = Guid.NewGuid();

        var done = await Run(gone.UnitId, unknown, here.UnitId);

        Assert.Equal(
            [ClearOutOutcome.Unchanged, ClearOutOutcome.Unchanged, ClearOutOutcome.CheckedOut],
            done.Units.Select(u => u.Outcome));
        Assert.Equal(UnitStatus.CheckedOut, units.Units[here.UnitId].Unit.Status);
        Assert.Equal("scanner", units.Units[gone.UnitId].Unit.CheckOutSource); // the earlier check-out is untouched
        Assert.False(units.Units.ContainsKey(unknown));
    }

    [Fact]
    public async Task A_failure_is_reported_for_that_unit_and_the_ones_that_worked_stay_checked_out()
    {
        var first = AddUnit();
        var broken = AddUnit();
        var last = AddUnit();
        units.FailOnReplace.Add(broken.UnitId);

        var done = await Run(first.UnitId, broken.UnitId, last.UnitId);

        Assert.Equal((2, 0, 1), (done.CheckedOut, done.Unchanged, done.Failed));
        Assert.Equal(
            [ClearOutOutcome.CheckedOut, ClearOutOutcome.Failed, ClearOutOutcome.CheckedOut],
            done.Units.Select(u => u.Outcome));
        Assert.Equal(UnitStatus.CheckedOut, units.Units[first.UnitId].Unit.Status);
        Assert.Equal(UnitStatus.InStock, units.Units[broken.UnitId].Unit.Status);
        Assert.Equal(UnitStatus.CheckedOut, units.Units[last.UnitId].Unit.Status);
    }

    [Fact]
    public async Task A_replayed_request_changes_nothing()
    {
        var a = AddUnit();
        var b = AddUnit();
        await Run(a.UnitId, b.UnitId);
        var before = units.Units.ToDictionary(u => u.Key, u => u.Value);

        var again = await Run(a.UnitId, b.UnitId);

        Assert.Equal((0, 2, 0), (again.CheckedOut, again.Unchanged, again.Failed));
        Assert.Equal(before, units.Units);
    }

    [Fact]
    public async Task Undo_of_the_units_a_batch_checked_out_puts_back_only_those()
    {
        var earlier = AddUnit(UnitStatus.CheckedOut);
        var a = AddUnit();
        var b = AddUnit();
        var done = await Run(earlier.UnitId, a.UnitId, b.UnitId);
        var undo = new UndoCheckOut(units, NullLogger<UndoCheckOut>.Instance);

        foreach (var unit in done.Units.Where(u => u.Outcome == ClearOutOutcome.CheckedOut))
        {
            await undo.ExecuteAsync(unit.UnitId, CancellationToken.None, Gary);
        }

        Assert.Equal(UnitStatus.InStock, units.Units[a.UnitId].Unit.Status);
        Assert.Equal(UnitStatus.InStock, units.Units[b.UnitId].Unit.Status);
        Assert.Equal(UnitStatus.CheckedOut, units.Units[earlier.UnitId].Unit.Status); // not part of the batch
    }

    [Fact]
    public async Task Refuses_nothing_too_many_or_a_repeated_unit()
    {
        var unit = AddUnit();

        Assert.IsType<CheckOutManyResult.Invalid>(await ClearOut().ExecuteAsync(null, Gary, CancellationToken.None));
        Assert.IsType<CheckOutManyResult.Invalid>(await ClearOut().ExecuteAsync([], Gary, CancellationToken.None));
        Assert.IsType<CheckOutManyResult.Invalid>(await ClearOut().ExecuteAsync(
            Enumerable.Range(0, CheckOutMany.MaxUnits + 1).Select(_ => Guid.NewGuid()).ToList(), Gary, CancellationToken.None));
        Assert.IsType<CheckOutManyResult.Invalid>(await ClearOut().ExecuteAsync([unit.UnitId, unit.UnitId], Gary, CancellationToken.None));
        Assert.Equal(UnitStatus.InStock, units.Units[unit.UnitId].Unit.Status);
    }

    [Fact]
    public async Task The_log_holds_counts_only()
    {
        var a = AddUnit();
        var broken = AddUnit();
        units.FailOnReplace.Add(broken.UnitId);

        await Run(a.UnitId, broken.UnitId, Guid.NewGuid());

        var line = Assert.Single(log.At(LogLevel.Information));
        Assert.Equal("Clear out: 3 units requested, 1 checked out, 1 unchanged, 1 failed.", line);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("Gravy", StringComparison.OrdinalIgnoreCase));
    }
}
