using CupboardTracker.Application.Digest;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.Digest;
using CupboardTracker.Application.Tests.ShelfLives;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Tests.Stock;

/// <summary>Opened, frozen and defrosted in item detail, Undo, "once opened" by hand, and the timer (#65).</summary>
public sealed class UnitStateUseCaseTests
{
    // 23:30 UTC on 17 Oct is 00:30 BST on 18 Oct: "today" is the 18th in the UK.
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 23, 30, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FakeEstimator estimator = new();
    private readonly FixedClock clock = new(Now);

    private ChangeUnitState Change() => new(units, products, TestSettings.Stock(), clock, new ListLogger<ChangeUnitState>());

    private UndoUnitStateChange Undo() => new(units, TestSettings.Stock(), clock, new ListLogger<UndoUnitStateChange>());

    private Product Pesto(ProductStateEstimates? states = null)
    {
        var id = Guid.NewGuid();
        var product = Product.Found(id, "8001234567890", new ProductDetails("Pesto", "Sacla", "190 g", null, []), Now.AddDays(-30), 1)
            .WithShelfLife(new ShelfLife(180, Storage.Cupboard, 0.7, false, "jev", Now.AddDays(-30))) with
        {
            StateEstimates = states,
        };
        products.Products[id] = product;
        return product;
    }

    private static readonly ProductStateEstimates Estimates = new(
        new StateShelfLife(4, Storage.Fridge, 0.8, false, "jev"), new StateShelfLife(90, Storage.Freezer, 0.7, false, "jev"),
        new StateShelfLife(2, Storage.Fridge, 0.6, false, "jev"), Now.AddDays(-29));

    private Unit Jar(Product product, DateOnly? expiry = null)
    {
        var id = Guid.NewGuid();
        var unit = new Unit(id, product.ProductId, "8001234567890", UnitStatus.InStock, id, "scanner-in",
            new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), null, null, null, expiry ?? new DateOnly(2027, 4, 8), null,
            ExpirySource.ShelfLife);
        units.Add(unit);
        return unit;
    }

    private Task<ChangeUnitStateResult> Mark(Unit unit, UnitState state, string? date = null) =>
        Change().ExecuteAsync(unit.UnitId, state, date, Gary, CancellationToken.None);

    [Fact]
    public async Task Opened_today_UK_by_default_saves_and_returns_the_unit()
    {
        var jar = Jar(Pesto(Estimates));

        var changed = Assert.IsType<ChangeUnitStateResult.Changed>(await Mark(jar, UnitState.Opened));

        Assert.Equal((new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 22), Storage.Fridge, true),
            (changed.Unit.OpenedOn, changed.Unit.EstimatedExpiryDate, changed.Unit.CurrentStorage, changed.Unit.CanUndoStateChange));
        Assert.Equal(new DateOnly(2026, 10, 22), units.Units[jar.UnitId].Unit.EstimatedExpiryDate);
    }

    [Fact]
    public async Task A_different_day_can_be_given_but_not_in_the_future_or_before_it_was_bought()
    {
        var jar = Jar(Pesto(Estimates));

        Assert.IsType<ChangeUnitStateResult.Invalid>(await Mark(jar, UnitState.Opened, "2026-10-19"));
        Assert.IsType<ChangeUnitStateResult.Invalid>(await Mark(jar, UnitState.Opened, "2026-10-09"));
        Assert.IsType<ChangeUnitStateResult.Invalid>(await Mark(jar, UnitState.Opened, "18/10/2026"));
        var changed = Assert.IsType<ChangeUnitStateResult.Changed>(await Mark(jar, UnitState.Opened, "2026-10-12"));
        Assert.Equal(new DateOnly(2026, 10, 16), changed.Unit.EstimatedExpiryDate);
    }

    [Fact]
    public async Task Not_found_not_in_stock_and_defrosting_a_unit_that_isnt_frozen_are_refused()
    {
        var product = Pesto(Estimates);
        Assert.IsType<ChangeUnitStateResult.NotFound>(
            await Change().ExecuteAsync(Guid.NewGuid(), UnitState.Opened, null, Gary, CancellationToken.None));
        Assert.IsType<ChangeUnitStateResult.Conflict>(await Mark(Jar(product), UnitState.Defrosted));
        var gone = Jar(product);
        units.Units[gone.UnitId] = (gone.CheckOutByUser(Gary.ObjectId, Now), 1);
        Assert.IsType<ChangeUnitStateResult.Conflict>(await Mark(gone, UnitState.Frozen));
    }

    [Fact]
    public async Task Repeating_an_action_writes_nothing()
    {
        var jar = Jar(Pesto(Estimates));
        await Mark(jar, UnitState.Frozen);
        var version = units.Units[jar.UnitId].Version;

        Assert.IsType<ChangeUnitStateResult.Changed>(await Mark(jar, UnitState.Frozen));

        Assert.Equal(version, units.Units[jar.UnitId].Version);
    }

    [Fact]
    public async Task Undo_restores_the_unit_exactly_and_then_has_nothing_to_undo()
    {
        var jar = Jar(Pesto(Estimates));
        await Change().ExecuteAsync(jar.UnitId, UnitState.Frozen, null, Gary, CancellationToken.None);

        var undone = Assert.IsType<ChangeUnitStateResult.Changed>(await Undo().ExecuteAsync(jar.UnitId, Gary, CancellationToken.None));

        Assert.Equal(jar, units.Units[jar.UnitId].Unit);
        Assert.False(undone.Unit.CanUndoStateChange);
        Assert.IsType<ChangeUnitStateResult.Conflict>(await Undo().ExecuteAsync(jar.UnitId, Gary, CancellationToken.None));
    }

    [Fact]
    public async Task With_no_estimate_yet_the_tap_works_and_the_timer_fills_it_in()
    {
        var product = Pesto(); // shelf life, but no state estimates yet
        var jar = Jar(product);

        var changed = Assert.IsType<ChangeUnitStateResult.Changed>(await Mark(jar, UnitState.Opened));
        Assert.True(changed.Unit.EstimatePending);
        Assert.Equal(new DateOnly(2027, 4, 8), changed.Unit.EstimatedExpiryDate);

        var run = await new EstimateShelfLives(products, units, estimator, TestSettings.Stock(), clock, new ListLogger<EstimateShelfLives>())
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal((1, 1), (run.StatesEstimated, run.PendingFilled));
        var filled = units.Units[jar.UnitId].Unit;
        Assert.Equal((new DateOnly(2026, 10, 22), false, Storage.Fridge), (filled.EstimatedExpiryDate, filled.EstimatePending, filled.CurrentStorage));
    }

    [Fact]
    public async Task The_timer_asks_once_per_product_with_its_usual_storage_and_keeps_a_manual_once_opened()
    {
        var pesto = Pesto();
        await new SetOpenedShelfLife(products, new ListLogger<SetOpenedShelfLife>())
            .ExecuteAsync(pesto.ProductId, 5, Gary, CancellationToken.None);
        var timer = new EstimateShelfLives(products, units, estimator, TestSettings.Stock(), clock, new ListLogger<EstimateShelfLives>());

        await timer.ExecuteAsync(CancellationToken.None);
        await timer.ExecuteAsync(CancellationToken.None);

        var (description, usual) = Assert.Single(estimator.StateCalls);
        Assert.Equal(("Pesto", Storage.Cupboard), (description.Name, usual));
        var states = products.Products[pesto.ProductId].States;
        Assert.Equal((5, true), (states.Opened!.Days, states.Opened.IsManual));
        Assert.Equal((90, Storage.Freezer), (states.Frozen!.Days, states.Frozen.Storage)); // the freezer, whatever Jev said
        Assert.Equal((2, true), (states.Defrosted!.Days, states.Defrosted.LowConfidence));
        Assert.NotNull(states.EstimatedAt);
    }

    [Fact]
    public async Task Jev_down_leaves_products_waiting_and_the_shelf_life_fill_leaves_state_units_alone()
    {
        var pesto = Pesto();
        estimator.StateAnswer = _ => new StateEstimatesResult.Unavailable("HTTP 529");
        var opened = Jar(pesto) with { EstimatedExpiryDate = null, ExpirySource = ExpirySource.Opened, OpenedOn = new DateOnly(2026, 10, 18), EstimatePending = true };
        units.Units[opened.UnitId] = (opened, 1);

        var run = await new EstimateShelfLives(products, units, estimator, TestSettings.Stock(), clock, new ListLogger<EstimateShelfLives>())
            .ExecuteAsync(CancellationToken.None);

        Assert.True(run.Stopped);
        Assert.True(products.Products[pesto.ProductId].NeedsStateEstimates);
        Assert.Equal((null, ExpirySource.Opened, true), (units.Units[opened.UnitId].Unit.EstimatedExpiryDate,
            units.Units[opened.UnitId].Unit.ExpirySource, units.Units[opened.UnitId].Unit.EstimatePending));
    }

    [Fact]
    public async Task Once_opened_by_hand_needs_a_known_product_and_sensible_days()
    {
        var setter = new SetOpenedShelfLife(products, new ListLogger<SetOpenedShelfLife>());
        Assert.IsType<SetShelfLifeResult.Invalid>(await setter.ExecuteAsync(Pesto().ProductId, 0, Gary, CancellationToken.None));
        Assert.IsType<SetShelfLifeResult.NotFound>(await setter.ExecuteAsync(Guid.NewGuid(), 5, Gary, CancellationToken.None));
        var unknown = Product.NotFound(Guid.NewGuid(), "5010012345674", Now, 1);
        products.Products[unknown.ProductId] = unknown;
        Assert.IsType<SetShelfLifeResult.Conflict>(await setter.ExecuteAsync(unknown.ProductId, 5, Gary, CancellationToken.None));
    }

    [Fact]
    public void Item_detail_counts_opened_and_frozen_units()
    {
        var today = new DateOnly(2026, 10, 18);
        var view = new ProductView(Guid.NewGuid(), null, ProductStatus.Known, "Pesto", null, null, 3, null, null,
        [
            ProductUnitView.From(Jar(Pesto()) with { OpenedOn = today }, today),
            ProductUnitView.From(Jar(Pesto()) with { FrozenOn = today }, today),
            ProductUnitView.From(Jar(Pesto()) with { FrozenOn = today, DefrostedOn = today }, today),
        ]);

        Assert.Equal((1, 1), (view.OpenedCount, view.FrozenCount));
    }

    [Fact]
    public async Task The_digest_says_when_a_use_first_item_was_opened()
    {
        var pesto = Pesto();
        var queries = new FakeDigestQueries();
        queries.InStock.Add(new DigestUnit(pesto.ProductId, "8001234567890", new DateOnly(2026, 10, 18), "opened 12 Oct"));
        queries.InStock.Add(new DigestUnit(pesto.ProductId, "8001234567890", new DateOnly(2027, 4, 8)));
        var digest = new SendDailyDigest(queries, products, new FakeSentDigests(), new CupboardTracker.Application.Tests.Hubs.FakeNotifier(),
            new DigestSettings(TestSettings.London), TestSettings.Stock(), clock, new ListLogger<SendDailyDigest>());

        var data = await digest.GatherAsync(DigestWindow.EndingOn(new DateOnly(2026, 10, 18), TimeZoneInfo.Utc), CancellationToken.None);

        Assert.Equal([new DigestUseFirstLine("Pesto", 2, "Est. today", "opened 12 Oct", "Other")], data.UseFirstLines);
        Assert.Contains("Pesto (opened 12 Oct) ×2 · Est. today", BuildDailyDigest.Build(data).PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stock_lists_Opened_opens_the_next_unit_to_use_skipping_frozen_and_not_when_one_is_open()
    {
        var product = Pesto(Estimates);
        var frozenSoonest = Jar(product, new DateOnly(2026, 10, 20));
        units.Units[frozenSoonest.UnitId] = (frozenSoonest with { FrozenOn = new DateOnly(2026, 10, 18) }, 1);
        Jar(product, new DateOnly(2027, 4, 8));
        var next = Jar(product, new DateOnly(2027, 1, 1));
        var open = new OpenNextUnit(units, Change());

        var opened = Assert.IsType<ChangeUnitStateResult.Changed>(await open.ExecuteAsync(product.ProductId, Gary, CancellationToken.None));

        Assert.Equal((next.UnitId, new DateOnly(2026, 10, 18)), (opened.Unit.UnitId, opened.Unit.OpenedOn));
        Assert.IsType<ChangeUnitStateResult.Conflict>(await open.ExecuteAsync(product.ProductId, Gary, CancellationToken.None));
        Assert.IsType<ChangeUnitStateResult.NotFound>(await open.ExecuteAsync(Guid.NewGuid(), Gary, CancellationToken.None));
    }

    [Fact]
    public async Task Nothing_to_open_when_every_unit_is_in_the_freezer()
    {
        var product = Pesto(Estimates);
        var jar = Jar(product);
        units.Units[jar.UnitId] = (jar with { FrozenOn = new DateOnly(2026, 10, 18) }, 1);

        Assert.IsType<ChangeUnitStateResult.Conflict>(
            await new OpenNextUnit(units, Change()).ExecuteAsync(product.ProductId, Gary, CancellationToken.None));
    }
}
