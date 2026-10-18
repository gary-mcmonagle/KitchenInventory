using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Application.Users;
using CupboardTracker.Application.Products;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.ShelfLives;

internal sealed class FakeEstimator : IShelfLifeEstimator
{
    public List<ProductDescription> Calls { get; } = [];

    public Func<ProductDescription, ShelfLifeResult> Answer { get; set; } =
        _ => new ShelfLifeResult.Estimated(new ShelfLifeEstimate(7, Storage.Fridge, 0.83, false));

    public Task<ShelfLifeResult> EstimateAsync(ProductDescription product, CancellationToken cancellationToken)
    {
        Calls.Add(product);
        return Task.FromResult(Answer(product));
    }

    public List<(ProductDescription Product, Storage Usual)> StateCalls { get; } = [];

    public Func<ProductDescription, StateEstimatesResult> StateAnswer { get; set; } =
        _ => new StateEstimatesResult.Estimated(new StateEstimates(
            new StateEstimate(4, Storage.Fridge, 0.8, false),
            new StateEstimate(90, Storage.Cupboard, 0.7, false), // the freezer is always the freezer
            new StateEstimate(2, Storage.Fridge, 0.4, true)));

    public Task<StateEstimatesResult> EstimateStatesAsync(ProductDescription product, Storage usualStorage, CancellationToken cancellationToken)
    {
        StateCalls.Add((product, usualStorage));
        return Task.FromResult(StateAnswer(product));
    }
}

public sealed class EstimateShelfLivesTests
{
    // 22:00 UTC on 17 Oct is 23:00 BST: still 17 Oct in the UK.
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 22, 0, 0, TimeSpan.Zero);
    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FakeEstimator estimator = new();
    private readonly ListLogger<EstimateShelfLives> logger = new();

    private EstimateShelfLives UseCase() =>
        new(products, units, estimator, TestSettings.Stock(), new FixedClock(Now), logger);

    private Guid Known(string name, string barcode = "4056489899112", string[]? tags = null)
    {
        var id = Guid.NewGuid();
        products.Products[id] = Product.Found(
            id, barcode, new ProductDetails(name, "Dairy Manor", "2.272 L", null, tags ?? ["en:milks"]), Now, 1);
        return id;
    }

    private Unit InStock(Guid productId, DateTimeOffset checkedInAt, UnitStatus status = UnitStatus.InStock)
    {
        var id = Guid.NewGuid();
        var unit = new Unit(id, productId, "4056489899112", status, id, "scanner-in", checkedInAt, null, null, null, null);
        units.Add(unit);
        return unit;
    }

    [Fact]
    public async Task Estimates_known_products_once_and_fills_their_in_stock_units()
    {
        var milk = Known("SEMI-SKIMMED BRITISH MILK", tags: ["en:dairies", "en:milks"]);
        // Bought late on 14 Oct UK time (23:30 BST is 22:30 UTC).
        var a = InStock(milk, new DateTimeOffset(2026, 10, 14, 22, 30, 0, TimeSpan.Zero));
        var b = InStock(milk, new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero));
        var gone = InStock(milk, Now.AddDays(-3), UnitStatus.CheckedOut);

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(new ShelfLifeRun(1, 0, 2, false, StatesEstimated: 1), run); // and its state estimates (#65)
        var call = Assert.Single(estimator.Calls); // once per product, not per unit
        Assert.Equal(("SEMI-SKIMMED BRITISH MILK", "Dairy Manor", "2.272 L"), (call.Name, call.Brand, call.Size));
        Assert.Equal(["en:dairies", "en:milks"], call.CategoryTags);
        var shelfLife = products.Products[milk].ShelfLife!;
        Assert.Equal((7, Storage.Fridge, 0.83, false, "jev", Now),
            (shelfLife.Days, shelfLife.UsualStorage, shelfLife.Confidence, shelfLife.LowConfidence, shelfLife.Source, shelfLife.EstimatedAt));
        Assert.Equal(new DateOnly(2026, 10, 21), units.Units[a.UnitId].Unit.EstimatedExpiryDate);
        Assert.Equal(new DateOnly(2026, 10, 23), units.Units[b.UnitId].Unit.EstimatedExpiryDate);
        Assert.Null(units.Units[gone.UnitId].Unit.EstimatedExpiryDate); // checked-out units are left alone
    }

    [Fact]
    public async Task Running_again_changes_nothing()
    {
        var milk = Known("Milk");
        InStock(milk, Now.AddDays(-1));
        await UseCase().ExecuteAsync(CancellationToken.None);
        var before = units.Units.Values.Select(v => v.Version).ToList();

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(new ShelfLifeRun(0, 0, 0, false), run);
        Assert.Single(estimator.Calls);
        Assert.Equal(before, units.Units.Values.Select(v => v.Version));
    }

    [Fact]
    public async Task Unknown_items_get_no_shelf_life()
    {
        var id = Guid.NewGuid();
        products.Products[id] = Product.NotFound(id, "20055363", Now, 1);
        InStock(id, Now.AddDays(-1));

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(new ShelfLifeRun(0, 0, 0, false), run);
        Assert.Empty(estimator.Calls);
    }

    [Fact]
    public async Task An_item_named_in_the_app_is_estimated_on_the_next_run()
    {
        var id = Guid.NewGuid();
        products.Products[id] = Product.NotFound(id, "20055363", Now, 1);
        var unit = InStock(id, Now.AddDays(-1));
        await new NameProduct(products, new FakeFeed().Notifier(), new FixedClock(Now), new ListLogger<NameProduct>()).ExecuteAsync(
            id, new NameProductCommand("Plain naan", null, null), new CurrentUser(Guid.NewGuid(), "Gary"), CancellationToken.None);

        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal("Plain naan", Assert.Single(estimator.Calls).Name);
        Assert.Equal(new DateOnly(2026, 10, 23), units.Units[unit.UnitId].Unit.EstimatedExpiryDate);
    }

    [Fact]
    public async Task Low_confidence_estimates_are_counted_and_kept_flagged()
    {
        Known("PLAIN NAAN BREADS");
        estimator.Answer = _ => new ShelfLifeResult.Estimated(new ShelfLifeEstimate(4, Storage.Freezer, 0.16, true));

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, run.LowConfidence);
        Assert.True(products.Products.Values.Single().ShelfLife!.LowConfidence);
    }

    [Fact]
    public async Task When_jev_is_unreachable_products_stay_unestimated_and_the_next_run_retries()
    {
        var milk = Known("Milk");
        Known("Bread", barcode: "20719395");
        InStock(milk, Now.AddDays(-1));
        estimator.Answer = _ => new ShelfLifeResult.Unavailable("HTTP 529 (after 3 attempts)");

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.True(run.Stopped);
        Assert.Single(estimator.Calls); // stops at the first: the rest would fail too
        Assert.All(products.Products.Values, p => Assert.Null(p.ShelfLife));
        Assert.Contains(logger.At(LogLevel.Warning), m => m.Contains("couldn't be reached", StringComparison.Ordinal));
        Assert.Empty(logger.At(LogLevel.Error));

        estimator.Answer = _ => new ShelfLifeResult.Estimated(new ShelfLifeEstimate(7, Storage.Fridge, 0.8, false));
        var retry = await UseCase().ExecuteAsync(CancellationToken.None);
        Assert.Equal((2, 1), (retry.Estimated, retry.UnitsFilled));
    }

    [Fact]
    public async Task Without_a_key_it_warns_and_waits()
    {
        Known("Milk");
        estimator.Answer = _ => new ShelfLifeResult.NotConfigured();

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.True(run.Stopped);
        Assert.Contains(logger.At(LogLevel.Warning), m => m.Contains("isn't configured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_request_logs_an_error()
    {
        Known("Milk");
        estimator.Answer = _ => new ShelfLifeResult.Failed("HTTP 422");

        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Contains(logger.At(LogLevel.Error), m => m.Contains("HTTP 422", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_product_estimated_meanwhile_is_not_overwritten()
    {
        var milk = Known("Milk");
        var theirs = new ShelfLife(10, Storage.Fridge, 0.9, false, "jev", Now.AddMinutes(-1));
        products.BeforeWrite = () => products.Products[milk] = products.Products[milk].WithShelfLife(theirs);

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, run.Estimated);
        Assert.Equal(theirs, products.Products[milk].ShelfLife);
    }

    [Fact]
    public async Task Does_a_limited_number_of_products_per_run()
    {
        for (var i = 0; i < EstimateShelfLives.MaxPerRun + 5; i++)
        {
            Known($"Product {i}", barcode: $"50000000{i:D5}");
        }

        Assert.Equal(EstimateShelfLives.MaxPerRun, (await UseCase().ExecuteAsync(CancellationToken.None)).Estimated);
        Assert.Equal(5, (await UseCase().ExecuteAsync(CancellationToken.None)).Estimated);
    }
}
