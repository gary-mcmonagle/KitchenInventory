using CupboardTracker.Application.Digest;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.Digest;
using CupboardTracker.Application.Tests.ShelfLives;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Tests.Stock;

/// <summary>Aisles (#71) and product icons (#72).</summary>
public sealed class AislesAndIconsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 18);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FakeEstimator estimator = new();
    private readonly FixedClock clock = new(Now);

    private EstimateShelfLives Timer() => new(products, units, estimator, TestSettings.Stock(), clock, new ListLogger<EstimateShelfLives>());

    private Product Known(string name, ProductAisle? aisle = null, ProductIcon? icon = null)
    {
        var id = Guid.NewGuid();
        var product = Product.Found(id, "5000354922831", new ProductDetails(name, "Brand", null, null, []), Now, 1) with
        {
            ShelfLife = new ShelfLife(30, Storage.Cupboard, 0.7, false, "jev", Now), Aisle = aisle, Icon = icon,
        };
        products.Products[id] = product;
        return product;
    }

    [Fact]
    public async Task The_timer_asks_aisle_and_icon_together_once_per_product()
    {
        var milk = Known("Milk");

        await Timer().ExecuteAsync(CancellationToken.None);
        await Timer().ExecuteAsync(CancellationToken.None);

        var call = Assert.Single(estimator.ClassifyCalls);
        Assert.Equal(("Milk", Storage.Cupboard, true, true), (call.Product.Name, call.Usual, call.Aisle, call.Icon));
        var stored = products.Products[milk.ProductId];
        Assert.Equal(("dairyEggs", "jev", "milk", "jev"), (stored.Aisle!.Id, stored.Aisle.Source, stored.Icon!.Name, stored.Icon.Source));
    }

    [Fact]
    public async Task An_unsure_icon_is_recorded_without_a_name_so_the_aisle_icon_shows_and_it_isnt_asked_again()
    {
        var gravy = Known("Beef Gravy");
        estimator.ClassifyAnswer = _ => new ClassificationResult.Classified(new Classification("saucesOils", 0.6, "pot", 0.28));

        await Timer().ExecuteAsync(CancellationToken.None);
        await Timer().ExecuteAsync(CancellationToken.None);

        var icon = products.Products[gravy.ProductId].Icon!;
        Assert.Equal((null, 0.28), (icon.Name, icon.Confidence));
        Assert.Single(estimator.ClassifyCalls);
    }

    [Fact]
    public async Task Only_whats_missing_is_asked_and_a_manual_choice_is_never_overwritten()
    {
        var mine = Known("Leeks", aisle: new ProductAisle("fruitVeg", "manual", 1, Now));

        await Timer().ExecuteAsync(CancellationToken.None);

        Assert.Equal((false, true), (estimator.ClassifyCalls[0].Aisle, estimator.ClassifyCalls[0].Icon));
        Assert.Equal(("fruitVeg", "manual"), (products.Products[mine.ProductId].Aisle!.Id, products.Products[mine.ProductId].Aisle!.Source));
    }

    [Fact]
    public async Task Gary_sets_an_aisle_and_icon_by_hand_and_let_jev_choose_asks_again()
    {
        var bread = Known("Sourdough", new ProductAisle("other", "jev", 0.4, Now), new ProductIcon("cake", "jev", 0.5, Now));
        var setAisle = new SetProductAisle(products, clock, new ListLogger<SetProductAisle>());
        var setIcon = new SetProductIcon(products, clock, new ListLogger<SetProductIcon>());

        Assert.IsType<SetClassificationResult.Saved>(await setAisle.ExecuteAsync(bread.ProductId, "bakery", Gary, CancellationToken.None));
        Assert.IsType<SetClassificationResult.Saved>(await setIcon.ExecuteAsync(bread.ProductId, "bread", Gary, CancellationToken.None));
        Assert.Equal(("bakery", "manual", "bread", "manual"), (products.Products[bread.ProductId].Aisle!.Id,
            products.Products[bread.ProductId].Aisle!.Source, products.Products[bread.ProductId].Icon!.Name, products.Products[bread.ProductId].Icon!.Source));

        Assert.IsType<SetClassificationResult.Saved>(await setIcon.ExecuteAsync(bread.ProductId, null, Gary, CancellationToken.None));
        await Timer().ExecuteAsync(CancellationToken.None);
        Assert.Equal((false, true), (estimator.ClassifyCalls[0].Aisle, estimator.ClassifyCalls[0].Icon));
    }

    [Fact]
    public async Task Unknown_aisles_and_icons_are_refused_and_unnamed_products_are_a_conflict()
    {
        var p = Known("Milk");
        Assert.IsType<SetClassificationResult.Invalid>(
            await new SetProductAisle(products, clock, new ListLogger<SetProductAisle>()).ExecuteAsync(p.ProductId, "garden", Gary, CancellationToken.None));
        Assert.IsType<SetClassificationResult.Invalid>(
            await new SetProductIcon(products, clock, new ListLogger<SetProductIcon>()).ExecuteAsync(p.ProductId, "rocket", Gary, CancellationToken.None));
        var unknown = Product.NotFound(Guid.NewGuid(), "5010012345674", Now, 1);
        products.Products[unknown.ProductId] = unknown;
        Assert.IsType<SetClassificationResult.Conflict>(
            await new SetProductAisle(products, clock, new ListLogger<SetProductAisle>()).ExecuteAsync(unknown.ProductId, "bakery", Gary, CancellationToken.None));
        Assert.IsType<SetClassificationResult.NotFound>(
            await new SetProductIcon(products, clock, new ListLogger<SetProductIcon>()).ExecuteAsync(Guid.NewGuid(), "bread", Gary, CancellationToken.None));
    }

    [Fact]
    public async Task The_stock_list_is_grouped_by_aisle_in_shop_order_with_use_first_first_and_unknown_items_apart()
    {
        var queries = new FakeStockQueries();
        var naan = Known("Naan", new ProductAisle("bakery", "jev", 0.9, Now), new ProductIcon("bread", "jev", 0.9, Now));
        var loaf = Known("Loaf", new ProductAisle("bakery", "jev", 0.9, Now));
        var crumpets = Known("Crumpets", new ProductAisle("bakery", "jev", 0.9, Now));
        var apples = Known("Apples", new ProductAisle("fruitVeg", "jev", 0.9, Now));
        var mystery = Known("Mystery"); // no aisle yet: Other
        var unknown = Product.NotFound(Guid.NewGuid(), "5010012345674", Now, 1);
        products.Products[unknown.ProductId] = unknown;
        queries.Units.AddRange([
            new StockUnit(naan.ProductId, null, Now, Today.AddDays(20)),
            new StockUnit(loaf.ProductId, null, Now, Today.AddDays(5)),
            new StockUnit(crumpets.ProductId, null, Now, Today.AddDays(-1)), // expired: use first
            new StockUnit(apples.ProductId, null, Now, Today.AddDays(2)),
            new StockUnit(mystery.ProductId, null, Now),
            new StockUnit(unknown.ProductId, "5010012345674", Now),
        ]);

        var view = await new GetStock(queries, products, TestSettings.Stock(), clock).ExecuteAsync(CancellationToken.None);

        Assert.Equal(["fruitVeg", "bakery", "other"], view.Aisles.Select(a => a.AisleId));
        var bakery = view.Aisles[1];
        Assert.Equal(["Crumpets", "Loaf", "Naan"], bakery.Products.Select(p => p.DisplayName));
        Assert.Equal((3, 1, true, "bread"), (bakery.ItemCount, bakery.UseFirstCount, bakery.AnyExpired, bakery.Icon));
        Assert.Equal("bread", bakery.Products[2].Icon);
        Assert.Equal((1, false), (view.Aisles[0].UseFirstCount, view.Aisles[0].AnyExpired));
        Assert.Equal([unknown.ProductId], view.WaitingForName.Select(p => p.ProductId));
        Assert.Equal(2, view.UseFirstCount);
    }

    [Fact]
    public async Task The_digest_groups_use_first_by_aisle_with_small_headings()
    {
        var queries = new FakeDigestQueries();
        var milk = Known("Milk", new ProductAisle("dairyEggs", "jev", 0.9, Now));
        var leeks = Known("Leeks", new ProductAisle("fruitVeg", "jev", 0.9, Now));
        queries.InStock.Add(new DigestUnit(milk.ProductId, null, Today.AddDays(1)));
        queries.InStock.Add(new DigestUnit(leeks.ProductId, null, Today.AddDays(2)));
        var digest = new SendDailyDigest(queries, products, new FakeSentDigests(), new CupboardTracker.Application.Tests.Hubs.FakeNotifier(),
            new DigestSettings(TestSettings.London), TestSettings.Stock(), clock, new ListLogger<SendDailyDigest>());

        var data = await digest.GatherAsync(DigestWindow.EndingOn(Today, TimeZoneInfo.Utc), CancellationToken.None);
        var text = BuildDailyDigest.Build(data).PlainText.ReplaceLineEndings("\n");

        Assert.Equal(["Leeks", "Milk"], data.UseFirstLines.Select(l => l.Name)); // shop order: fruit & veg first
        Assert.Contains("Use first\nFruit & veg:\n  Leeks ×1 · Est. 2 days\nDairy & eggs:\n  Milk ×1 · Est. tomorrow\n", text, StringComparison.Ordinal);
    }
}
