using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Tests.Stock;

internal sealed class FakeStockQueries : IStockQueries
{
    public List<StockUnit> Units { get; } = [];

    public Task<IReadOnlyList<StockUnit>> GetInStockUnitsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StockUnit>>(Units);
}

public sealed class GetStockTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 22, 0, 0, TimeSpan.Zero);
    private readonly FakeStockQueries queries = new();
    private readonly FakeProducts products = new();

    private GetStock UseCase(int useFirstDays = StockSettings.DefaultUseFirstDays) =>
        new(queries, products, TestSettings.Stock(useFirstDays), new FixedClock(Now));

    private Guid Known(string name, string? brand = null, string? size = null, string barcode = "5000354922831")
    {
        var id = Guid.NewGuid();
        products.Products[id] = Product.Found(id, barcode, new ProductDetails(name, brand, size, null, []), Now, 1);
        return id;
    }

    private Guid Unnamed(ProductStatus status, string barcode)
    {
        var id = Guid.NewGuid();
        products.Products[id] = status == ProductStatus.NoMatch
            ? Product.NotFound(id, barcode, Now, 1)
            : Product.Failed(id, barcode, Now, 1, null);
        return id;
    }

    private void InStock(Guid productId, int count, int hoursAgo = 1, string? barcode = null, int? expiresInDays = null)
    {
        for (var i = 0; i < count; i++)
        {
            queries.Units.Add(new StockUnit(productId, barcode, Now.AddHours(-hoursAgo - i),
                expiresInDays is { } days ? Today.AddDays(days + i) : null));
        }
    }

    // 22:00 UTC on 17 Oct is 23:00 BST: still the 17th in the UK.
    private static readonly DateOnly Today = new(2026, 10, 17);

    [Theory]
    [InlineData(-1, StockSection.UseFirst)]
    [InlineData(0, StockSection.UseFirst)]
    [InlineData(1, StockSection.UseFirst)]
    [InlineData(3, StockSection.UseFirst)]
    [InlineData(4, StockSection.Later)]
    [InlineData(400, StockSection.Later)]
    public async Task Use_first_is_expired_or_due_within_three_days(int days, StockSection section)
    {
        InStock(Known("Milk"), 1, expiresInDays: days);

        var view = await UseCase().ExecuteAsync(CancellationToken.None);

        var product = Assert.Single(view.Products);
        Assert.Equal((section, days, Today.AddDays(days)), (product.Section, product.DaysUntilSoonest, product.SoonestEstimatedExpiryDate));
        Assert.Equal(section == StockSection.UseFirst ? 1 : 0, view.UseFirstCount);
    }

    [Fact]
    public async Task The_threshold_comes_from_settings()
    {
        InStock(Known("Yoghurt"), 1, expiresInDays: 5);
        Assert.Equal(StockSection.UseFirst, Assert.Single((await UseCase(useFirstDays: 5).ExecuteAsync(CancellationToken.None)).Products).Section);
    }

    [Fact]
    public async Task The_soonest_unit_decides_and_the_shelf_life_is_included()
    {
        var milk = Known("Milk");
        products.Products[milk] = products.Products[milk].WithShelfLife(new ShelfLife(7, Storage.Fridge, 0.8, false, "jev", Now));
        InStock(milk, 3, expiresInDays: 2); // 2, 3 and 4 days

        var product = Assert.Single((await UseCase().ExecuteAsync(CancellationToken.None)).Products);

        Assert.Equal((2, 7, 3), (product.DaysUntilSoonest!.Value, product.ShelfLifeDays!.Value, product.Count));
    }

    [Fact]
    public async Task Sections_are_sorted_by_soonest_then_name_with_no_estimate_last_and_unnamed_at_the_bottom()
    {
        InStock(Known("Penne"), 1, expiresInDays: 700);
        InStock(Known("hummus"), 1, expiresInDays: -1);
        InStock(Known("Walnuts"), 1);                       // no estimate yet: Later, last
        InStock(Unnamed(ProductStatus.NoMatch, "20055363"), 1, hoursAgo: 3);
        InStock(Known("Chicken"), 1, expiresInDays: 1);
        InStock(Known("Baked beans"), 1, expiresInDays: 700);
        InStock(Known("Cheddar"), 1, expiresInDays: 17);
        InStock(Known("Apples"), 1);

        var view = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(
            ["hummus", "Chicken", "Cheddar", "Baked beans", "Penne", "Apples", "Walnuts", "Unknown item (20055363)"],
            view.Products.Select(p => p.DisplayName));
        Assert.Equal(
            [StockSection.UseFirst, StockSection.UseFirst, StockSection.Later, StockSection.Later, StockSection.Later,
             StockSection.Later, StockSection.Later, StockSection.WaitingForName],
            view.Products.Select(p => p.Section));
        Assert.Equal(2, view.UseFirstCount);
        Assert.Null(view.Products[5].DaysUntilSoonest);
    }

    [Fact]
    public async Task Groups_units_by_product_with_counts_and_details()
    {
        var gravy = Known("Beef Gravy", "Bisto", "230g");
        var naan = Known("PLAIN NAAN BREADS");
        InStock(gravy, 2);
        InStock(naan, 1);

        var view = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(3, view.TotalUnits);
        Assert.Equal(Now, view.GeneratedAt);
        var first = view.Products[0];
        Assert.Equal(("Beef Gravy", "Bisto", "230g", 2, StockProductStatus.Known),
            (first.DisplayName, first.Brand, first.Size, first.Count, first.Status));
        Assert.Equal(Now.AddHours(-1), first.LatestCheckedInAt);
        Assert.Null(view.Products[1].Brand);
        Assert.Null(view.Products[1].Size);
    }

    [Fact]
    public async Task Named_products_are_alphabetical_ignoring_case_and_unnamed_come_last_newest_first()
    {
        InStock(Unnamed(ProductStatus.NoMatch, "20055363"), 1, hoursAgo: 5);
        InStock(Known("walnut halves"), 1);
        InStock(Unnamed(ProductStatus.LookupFailed, "5059321008863"), 1, hoursAgo: 2);
        InStock(Known("Beef Gravy"), 1);
        InStock(Known("CHIPOTLE HOT SAUCE"), 1);

        var view = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(
            ["Beef Gravy", "CHIPOTLE HOT SAUCE", "walnut halves", "Unknown item (5059321008863)", "Unknown item (20055363)"],
            view.Products.Select(p => p.DisplayName));
        Assert.Equal(
            [StockProductStatus.Known, StockProductStatus.Known, StockProductStatus.Known,
             StockProductStatus.LookupFailed, StockProductStatus.NoMatch],
            view.Products.Select(p => p.Status));
        Assert.Equal("20055363", view.Products[^1].Barcode);
    }

    [Fact]
    public async Task Empty_stock()
    {
        var view = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, view.TotalUnits);
        Assert.Empty(view.Products);
    }

    [Fact]
    public async Task A_unit_whose_product_is_missing_still_shows_as_unnamed()
    {
        InStock(Guid.NewGuid(), 1, barcode: "4056489003703");

        var product = Assert.Single((await UseCase().ExecuteAsync(CancellationToken.None)).Products);

        Assert.Equal((StockProductStatus.LookupFailed, "4056489003703"), (product.Status, product.Barcode));
    }
}
