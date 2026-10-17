using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Products;

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

    private GetStock UseCase() => new(queries, products, new FixedClock(Now));

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

    private void InStock(Guid productId, int count, int hoursAgo = 1, string? barcode = null)
    {
        for (var i = 0; i < count; i++)
        {
            queries.Units.Add(new StockUnit(productId, barcode, Now.AddHours(-hoursAgo - i)));
        }
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
