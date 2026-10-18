using CupboardTracker.Application.Products;
using CupboardTracker.Application.Scans;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Tests.Products;

public sealed class NameProductTests
{
    private const string Barcode = "501001234567";
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FixedClock clock = new(TestScans.At);
    private readonly FakeLookup lookup = new();
    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();

    private NameProduct UseCase() => new(products, clock, new ListLogger<NameProduct>());

    private Guid Store(Product product)
    {
        products.Products[product.ProductId] = product;
        products.Lookups[Barcode] = product.ProductId;
        return product.ProductId;
    }

    private Guid NoMatch() => Store(Product.NotFound(Guid.NewGuid(), Barcode, TestScans.At.AddDays(-1), 1));

    private Task<NameProductResult> Name(Guid id, string? name = "Smoked paprika", string? brand = "Schwartz", string? size = "48 g") =>
        UseCase().ExecuteAsync(id, new NameProductCommand(name, brand, size), Gary, CancellationToken.None);

    [Fact]
    public async Task Naming_a_no_match_makes_it_known_and_manual()
    {
        var id = NoMatch();

        var result = Assert.IsType<NameProductResult.Named>(await Name(id, "  Smoked paprika  ", " Schwartz ", ""));

        Assert.Equal("Smoked paprika", result.DisplayName);
        var product = products.Products[id];
        Assert.Equal((ProductStatus.Known, ProductSource.Manual, "Smoked paprika"), (product.Status, product.Source, product.DisplayName));
        Assert.Equal(("Schwartz", (string?)null), (product.Details!.Brand, product.Details.Size));
        Assert.Equal(TestScans.At, product.NamedAt);
        Assert.Equal(Gary.ObjectId, product.NamedBy);
        Assert.Equal([Barcode], product.Barcodes);
    }

    [Fact]
    public async Task Naming_a_lookup_failed_product_ends_its_retries()
    {
        var id = Store(Product.Failed(Guid.NewGuid(), Barcode, TestScans.At, 3, TestScans.At.AddHours(-2)));

        Assert.IsType<NameProductResult.Named>(await Name(id));

        Assert.Null(products.Products[id].LookupFailedSince);
        Assert.Empty(await products.GetLookupFailedAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_known_product_cant_be_renamed_here()
    {
        var id = Store(Product.Found(Guid.NewGuid(), Barcode, new ProductDetails("Paprika", "Tesco", null, null, []), TestScans.At, 1));

        var result = Assert.IsType<NameProductResult.AlreadyNamed>(await Name(id));

        Assert.Equal("Paprika", result.DisplayName);
        Assert.Equal("Paprika", products.Products[id].DisplayName);
    }

    [Fact]
    public async Task Named_by_someone_else_or_found_by_the_retry_timer_meanwhile_is_a_conflict()
    {
        var id = NoMatch();
        products.BeforeWrite = () => products.Products[id] = products.Products[id] with { Status = ProductStatus.Known, DisplayName = "Paprika" };

        Assert.IsType<NameProductResult.AlreadyNamed>(await Name(id));
        Assert.Equal("Paprika", products.Products[id].DisplayName);
    }

    [Theory]
    [InlineData(null, null, null, "name")]
    [InlineData("   ", null, null, "name")]
    [InlineData("x", "this brand name is far too long to fit in sixty characters, really", null, "brand")]
    [InlineData("x", null, "this size description is far too long to fit in sixty characters", "size")]
    public async Task Invalid_details_are_reported_by_field(string? name, string? brand, string? size, string field)
    {
        var id = NoMatch();

        var result = Assert.IsType<NameProductResult.Invalid>(await Name(id, name, brand, size));

        Assert.True(result.Errors.ContainsKey(field));
        Assert.True(products.Products[id].IsUnnamed);
    }

    [Fact]
    public async Task A_name_over_100_characters_is_invalid() =>
        Assert.IsType<NameProductResult.Invalid>(await Name(NoMatch(), new string('a', 101)));

    [Fact]
    public async Task An_unknown_product_is_not_found() =>
        Assert.IsType<NameProductResult.NotFound>(await Name(Guid.NewGuid()));

    [Fact]
    public async Task A_later_check_in_uses_the_manual_name_with_no_lookup()
    {
        var resolver = new ProductResolver(products, lookup, clock, new ListLogger<ProductResolver>());
        var checkIn = new CheckInScan(resolver, units, TestSettings.Stock(), new ListLogger<CheckInScan>());
        await checkIn.ExecuteAsync(TestScans.Make(Barcode, Role.In), CancellationToken.None); // no match on OFF
        Assert.Single(lookup.Calls);
        var id = products.Lookups[Barcode];

        await Name(id);
        await checkIn.ExecuteAsync(TestScans.Make(Barcode, Role.In), CancellationToken.None);

        Assert.Single(lookup.Calls); // no second Open Food Facts call
        Assert.Equal(2, units.InStock(id).Count());
        Assert.Equal("Smoked paprika", products.Products[id].DisplayName);
    }
}

public sealed class GetProductTests
{
    private sealed class Devices : IDeviceRepository
    {
        public Task<Device?> GetAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult<Device?>(new Device(deviceId, deviceId == "scanner-in" ? Role.In : Role.Out, TestScans.At, null));

        public Task UpsertAsync(Device device, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Shows_the_product_with_its_stock_and_latest_check_in()
    {
        var products = new FakeProducts();
        var units = new FakeUnits();
        var id = Guid.NewGuid();
        products.Products[id] = Product.NotFound(id, "501001234567", TestScans.At, 1);
        foreach (var hoursAgo in new[] { 30, 2 })
        {
            units.Add(new Unit(Guid.NewGuid(), id, "501001234567", UnitStatus.InStock, Guid.NewGuid(), "scanner-in",
                TestScans.At.AddHours(-hoursAgo), null, null, null, null));
        }

        var view = await new GetProduct(products, units, new Devices()).ExecuteAsync(id, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(("501001234567", ProductStatus.NoMatch, 2), (view.Barcode, view.Status, view.InStockCount));
        Assert.Equal(TestScans.At.AddHours(-2), view.LatestCheckedInAt);
        Assert.Equal(Role.In, view.LatestDeviceRole);
        Assert.Null(view.Brand);
    }

    [Fact]
    public async Task Unknown_product() =>
        Assert.Null(await new GetProduct(new FakeProducts(), new FakeUnits(), new Devices()).ExecuteAsync(Guid.NewGuid(), CancellationToken.None));
}
