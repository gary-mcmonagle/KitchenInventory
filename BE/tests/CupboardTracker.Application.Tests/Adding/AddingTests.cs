using CupboardTracker.Application.Adding;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Tests.Adding;

internal sealed class FakeRecogniser : IProductPhotoRecogniser
{
    public PhotoRecognitionResult Result { get; set; } = new PhotoRecognitionResult.Recognised(new PhotoRecognition(true, []));

    public int Calls { get; private set; }

    public Task<PhotoRecognitionResult> RecogniseAsync(ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

public sealed class AddToStockTests
{
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FixedClock clock = new(TestScans.At);
    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();

    private AddToStock UseCase() =>
        new(products, units, new ProductMatcher(products), TestSettings.Stock(), clock, new ListLogger<AddToStock>());

    private Product Loose(string name, int? shelfLifeDays = null)
    {
        var product = Product.Loose(Guid.NewGuid(), name, null, null, ProductSource.Manual, Gary.ObjectId, TestScans.At.AddDays(-5));
        if (shelfLifeDays is { } days)
        {
            product = product with { ShelfLife = ShelfLife.Manual(days, Storage.Fridge, TestScans.At.AddDays(-5)) };
        }

        products.Products[product.ProductId] = product;
        return product;
    }

    private Task<AddToStockResult> Add(Guid requestId, params AddItem[] items) =>
        UseCase().ExecuteAsync(requestId, items, Gary, CancellationToken.None);

    private static AddItem Typed(string name, int quantity = 1, AddSource source = AddSource.Typed) =>
        new(null, name, null, null, quantity, source);

    [Fact]
    public async Task A_new_name_becomes_a_loose_product_with_units_added_now_by_the_user()
    {
        var added = Assert.IsType<AddToStockResult.Added>(await Add(Guid.NewGuid(), Typed("  Leeks ", 3, AddSource.Photo)));

        var item = Assert.Single(added.Items);
        Assert.Equal(("Leeks", 3, true), (item.DisplayName, item.Quantity, item.CreatedProduct));
        var product = products.Products[item.ProductId];
        Assert.Equal((ProductStatus.Known, ProductSource.Photo, true, Gary.ObjectId), (product.Status, product.Source, product.IsLoose, product.NamedBy));
        Assert.Equal(TestScans.At, product.LastAddedAt);
        var stock = units.InStock(item.ProductId).ToList();
        Assert.Equal(3, stock.Count);
        Assert.All(stock, u => Assert.Equal((TestScans.At, Gary.ObjectId, (string?)null, (DateOnly?)null), (u.CheckedInAt, u.CheckedInBy, u.Barcode, u.EstimatedExpiryDate)));
    }

    [Fact]
    public async Task A_name_matches_an_existing_product_ignoring_case_and_spaces_and_uses_its_shelf_life()
    {
        var leeks = Loose("Leeks", shelfLifeDays: 7);

        var added = Assert.IsType<AddToStockResult.Added>(await Add(Guid.NewGuid(), Typed("leeks  ")));

        Assert.Equal((leeks.ProductId, false), (added.Items[0].ProductId, added.Items[0].CreatedProduct));
        Assert.Single(products.Products);
        var unit = Assert.Single(units.InStock(leeks.ProductId));
        Assert.Equal((new DateOnly(2026, 10, 24), ExpirySource.ShelfLife), (unit.EstimatedExpiryDate, unit.ExpirySource));
    }

    [Fact]
    public async Task Picking_by_id_adds_to_that_product()
    {
        var bread = Loose("Sourdough loaf");

        var added = Assert.IsType<AddToStockResult.Added>(await Add(Guid.NewGuid(), new AddItem(bread.ProductId, null, null, null, 2, AddSource.Pick)));

        Assert.Equal(bread.ProductId, added.Items[0].ProductId);
        Assert.Equal(2, units.InStock(bread.ProductId).Count());
    }

    [Fact]
    public async Task A_retry_with_the_same_request_id_adds_nothing_twice()
    {
        var requestId = Guid.NewGuid();
        Loose("Bananas");

        var first = Assert.IsType<AddToStockResult.Added>(await Add(requestId, Typed("Baguette"), Typed("Bananas", 2)));
        var again = Assert.IsType<AddToStockResult.Added>(await Add(requestId, Typed("Baguette"), Typed("Bananas", 2)));

        Assert.Equal(first.Items.Select(i => i.ProductId), again.Items.Select(i => i.ProductId));
        Assert.Equal(2, products.Products.Count);
        Assert.Equal(3, units.Units.Count);
    }

    [Fact]
    public async Task An_unknown_or_unnamed_product_id_is_invalid_and_adds_nothing()
    {
        var unnamed = Product.NotFound(Guid.NewGuid(), "5010012345674", TestScans.At, 1);
        products.Products[unnamed.ProductId] = unnamed;

        foreach (var id in new[] { Guid.NewGuid(), unnamed.ProductId })
        {
            var invalid = Assert.IsType<AddToStockResult.Invalid>(await Add(Guid.NewGuid(), new AddItem(id, null, null, null, 1, AddSource.Pick)));
            Assert.True(invalid.Errors.ContainsKey("items[0].productId"));
        }

        Assert.Empty(units.Units);
    }

    [Theory]
    [InlineData("", 1, "items[0].name")]
    [InlineData("Leeks", 0, "items[0].quantity")]
    [InlineData("Leeks", 21, "items[0].quantity")]
    public async Task Invalid_rows_are_reported_by_field(string name, int quantity, string field)
    {
        var invalid = Assert.IsType<AddToStockResult.Invalid>(await Add(Guid.NewGuid(), Typed(name, quantity)));
        Assert.True(invalid.Errors.ContainsKey(field));
        Assert.Empty(products.Products);
    }

    [Fact]
    public async Task A_request_needs_an_id_and_one_to_twenty_items()
    {
        Assert.True(Assert.IsType<AddToStockResult.Invalid>(await Add(Guid.Empty, Typed("Leeks"))).Errors.ContainsKey("requestId"));
        Assert.True(Assert.IsType<AddToStockResult.Invalid>(await Add(Guid.NewGuid())).Errors.ContainsKey("items"));
        Assert.True(Assert.IsType<AddToStockResult.Invalid>(
            await Add(Guid.NewGuid(), Enumerable.Range(0, 21).Select(i => Typed($"Item {i}")).ToArray())).Errors.ContainsKey("items"));
    }

    [Fact]
    public void Deterministic_ids_are_stable_distinct_and_version_8()
    {
        var request = Guid.Parse("0b8e7a4e-8d0c-4f43-9a51-4f0f7a0e7b11");
        var a = AddToStock.DeterministicId(request, "unit:0:0");

        Assert.Equal(a, AddToStock.DeterministicId(request, "unit:0:0"));
        Assert.NotEqual(a, AddToStock.DeterministicId(request, "unit:0:1"));
        Assert.Equal('8', a.ToString("D")[14]);
    }
}

public sealed class ProductMatcherTests
{
    private readonly FakeProducts products = new();

    private Product Store(Product product)
    {
        products.Products[product.ProductId] = product;
        return product;
    }

    [Fact]
    public async Task Prefers_a_loose_product_then_the_most_recently_named()
    {
        Store(Product.Found(Guid.NewGuid(), "5010012345674", new ProductDetails("Leeks", null, null, null, []), TestScans.At, 1));
        Store(Product.Loose(Guid.NewGuid(), "LEEKS", null, null, ProductSource.Manual, Guid.NewGuid(), TestScans.At.AddDays(-3)));
        var newer = Store(Product.Loose(Guid.NewGuid(), "leeks", null, null, ProductSource.Manual, Guid.NewGuid(), TestScans.At));

        Assert.Equal(newer.ProductId, (await new ProductMatcher(products).FindAsync(" Leeks ", CancellationToken.None))?.ProductId);
    }

    [Fact]
    public async Task Unnamed_products_and_other_names_never_match()
    {
        Store(Product.NotFound(Guid.NewGuid(), "5010012345674", TestScans.At, 1));
        Store(Product.Loose(Guid.NewGuid(), "Leek and potato soup", null, null, ProductSource.Manual, Guid.NewGuid(), TestScans.At));

        Assert.Null(await new ProductMatcher(products).FindAsync("Leeks", CancellationToken.None));
    }
}

public sealed class RecogniseProductPhotoTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly FakeProducts products = new();
    private readonly FakeRecogniser recogniser = new();
    private readonly ListLogger<RecogniseProductPhoto> logger = new();

    private RecogniseProductPhoto UseCase() => new(recogniser, new ProductMatcher(products), logger);

    [Fact]
    public async Task Items_come_back_with_their_existing_product_when_the_name_matches()
    {
        var leeks = Product.Loose(Guid.NewGuid(), "Leeks", null, null, ProductSource.Manual, Guid.NewGuid(), TestScans.At);
        products.Products[leeks.ProductId] = leeks;
        recogniser.Result = new PhotoRecognitionResult.Recognised(new PhotoRecognition(true, [
            new RecognisedItem("leeks", null, null, ItemKind.Loose, 2),
            new RecognisedItem("Pitted green olives", "Crespo", "340 g", ItemKind.Packaged, 1),
        ]));

        var result = Assert.IsType<RecogniseProductPhotoResult.Recognised>(await UseCase().ExecuteAsync(Jpeg, CancellationToken.None));

        Assert.True(result.Readable);
        Assert.Equal([leeks.ProductId, null], result.Items.Select(i => i.MatchProductId));
        Assert.Equal("Leeks", result.Items[0].MatchDisplayName);
    }

    [Fact]
    public async Task Not_a_jpeg_empty_or_too_big_is_invalid_without_calling_the_model()
    {
        Assert.IsType<RecogniseProductPhotoResult.Invalid>(await UseCase().ExecuteAsync(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, CancellationToken.None));
        Assert.IsType<RecogniseProductPhotoResult.Invalid>(await UseCase().ExecuteAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        var huge = new byte[RecogniseProductPhoto.MaxBytes + 1];
        Jpeg.CopyTo(huge, 0);
        Assert.IsType<RecogniseProductPhotoResult.Invalid>(await UseCase().ExecuteAsync(huge, CancellationToken.None));
        Assert.Equal(0, recogniser.Calls);
    }

    [Fact]
    public async Task An_unavailable_model_is_a_warning_not_an_error()
    {
        recogniser.Result = new PhotoRecognitionResult.Unavailable("HTTP 503 (after 3 attempts)");

        Assert.IsType<RecogniseProductPhotoResult.Unavailable>(await UseCase().ExecuteAsync(Jpeg, CancellationToken.None));
        Assert.Single(logger.At(Microsoft.Extensions.Logging.LogLevel.Warning));
        Assert.Empty(logger.At(Microsoft.Extensions.Logging.LogLevel.Error));
    }
}

public sealed class LooseProductsTests
{
    [Fact]
    public async Task Only_loose_named_products_most_recently_added_first_then_a_to_z()
    {
        var products = new FakeProducts();
        Product Store(string name, DateTimeOffset? lastAdded)
        {
            var p = Product.Loose(Guid.NewGuid(), name, null, null, ProductSource.Manual, Guid.NewGuid(), TestScans.At) with { LastAddedAt = lastAdded };
            products.Products[p.ProductId] = p;
            return p;
        }

        Store("Carrots", null);
        Store("Apples", null);
        Store("Bananas", TestScans.At.AddDays(-1));
        Store("Leeks", TestScans.At);
        var scanned = Product.Found(Guid.NewGuid(), "5010012345674", new ProductDetails("Gravy", null, null, null, []), TestScans.At, 1);
        products.Products[scanned.ProductId] = scanned;

        var list = await new GetLooseProducts(products).ExecuteAsync(CancellationToken.None);

        Assert.Equal(["Leeks", "Bananas", "Apples", "Carrots"], list.Select(p => p.DisplayName));
    }
}

public sealed class StoreLabelTests
{
    private const string Label = "2012345678903";
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FixedClock clock = new(TestScans.At);
    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FakeStoreLabels storeLabels = new();
    private readonly FakeFeed feed = new();

    private AssignStoreLabel UseCase() => new(storeLabels, units,
        new AddToStock(products, units, new ProductMatcher(products), TestSettings.Stock(), clock, new ListLogger<AddToStock>()),
        TestSettings.Stock(), feed.Notifier(), clock, new ListLogger<AssignStoreLabel>());

    private async Task<ScanEvent> CheckIn(DateTimeOffset at)
    {
        var scan = TestScans.Make(Label, Role.In, at: at);
        await new StoreLabelCheckInScan(storeLabels, feed.Notifier(), clock, new ListLogger<StoreLabelCheckInScan>())
            .ExecuteAsync(scan, CancellationToken.None);
        return scan;
    }

    private Task<AssignStoreLabelResult> Assign(Guid scanId, AddItem item) => UseCase().ExecuteAsync(scanId, item, Gary, CancellationToken.None);

    [Fact]
    public async Task Assigning_adds_one_unit_at_the_scan_time_with_the_scan_id_and_resolves_it()
    {
        var scannedAt = TestScans.At.AddHours(-2);
        var scan = await CheckIn(scannedAt);
        clock.Now = TestScans.At;

        var assigned = Assert.IsType<AssignStoreLabelResult.Assigned>(await Assign(scan.ScanId, new AddItem(null, "Ham hock terrine", null, null, 1, AddSource.Typed)));

        Assert.Equal((scan.ScanId, "Ham hock terrine"), (assigned.UnitId, assigned.DisplayName));
        var unit = units.Units[scan.ScanId].Unit;
        Assert.Equal((assigned.ProductId, scannedAt, Label, Gary.ObjectId, UnitStatus.InStock), (unit.ProductId, unit.CheckedInAt, unit.Barcode, unit.CheckedInBy, unit.Status));
        var label = storeLabels.Records[scan.ScanId].CheckIn;
        Assert.Equal((assigned.ProductId, Gary.ObjectId, TestScans.At), (label.ResolvedProductId, label.ResolvedBy, label.ResolvedAt));
        Assert.True(products.Products[assigned.ProductId].IsLoose); // the label's barcode is never claimed
        Assert.Equal(assigned.ProductId, Assert.IsType<StoreLabelResolved>(feed.Sent[^1]).ProductId);
    }

    [Fact]
    public async Task Assigning_again_to_the_same_product_changes_nothing_but_a_different_one_is_a_conflict()
    {
        var scan = await CheckIn(TestScans.At);
        var olives = Product.Loose(Guid.NewGuid(), "Olives", null, null, ProductSource.Manual, Gary.ObjectId, TestScans.At);
        products.Products[olives.ProductId] = olives;
        var pick = new AddItem(olives.ProductId, null, null, null, 1, AddSource.Pick);

        Assert.IsType<AssignStoreLabelResult.Assigned>(await Assign(scan.ScanId, pick));
        Assert.IsType<AssignStoreLabelResult.Assigned>(await Assign(scan.ScanId, pick));
        var conflict = Assert.IsType<AssignStoreLabelResult.AlreadyAssigned>(await Assign(scan.ScanId, new AddItem(null, "Feta", null, null, 1, AddSource.Typed)));

        Assert.Equal(olives.ProductId, conflict.ProductId);
        Assert.Single(units.Units);
        Assert.Equal(olives.ProductId, units.Units[scan.ScanId].Unit.ProductId);
    }

    [Fact]
    public async Task Unknown_scan_is_not_found_and_resolved_ones_leave_the_scanners_list()
    {
        Assert.IsType<AssignStoreLabelResult.NotFound>(await Assign(Guid.NewGuid(), new AddItem(null, "Ham", null, null, 1, AddSource.Typed)));

        var scan = await CheckIn(TestScans.At);
        Assert.Single(await storeLabels.ListUnresolvedAsync(CancellationToken.None));
        await Assign(scan.ScanId, new AddItem(null, "Ham", null, null, 1, AddSource.Typed));
        Assert.Empty(await storeLabels.ListUnresolvedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_replayed_check_in_after_assigning_doesnt_ask_again()
    {
        var scan = await CheckIn(TestScans.At);
        await Assign(scan.ScanId, new AddItem(null, "Ham", null, null, 1, AddSource.Typed));
        var before = feed.Sent.Count;

        await new StoreLabelCheckInScan(storeLabels, feed.Notifier(), clock, new ListLogger<StoreLabelCheckInScan>())
            .ExecuteAsync(scan, CancellationToken.None);

        Assert.Equal(before, feed.Sent.Count);
    }
}
