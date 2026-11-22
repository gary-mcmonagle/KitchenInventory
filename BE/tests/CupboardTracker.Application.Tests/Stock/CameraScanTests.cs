using CupboardTracker.Application.Adding;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Stock;

public sealed class CameraScanTests
{
    private const string Bisto = "5000354922831";
    private const string StoreLabel = "2012345678903";
    private static readonly ProductDetails BistoDetails = new("Beef Gravy", "Bisto", "230g", null, ["en:gravy"]);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FixedClock clock = new(TestScans.At);
    private readonly FakeLookup lookup = new();
    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FakeUnmatched unmatched = new();
    private readonly FakeStoreLabels storeLabels = new();
    private readonly FakeFeed feed = new();
    private readonly ListLogger<CameraScan> log = new();

    private CameraScan Camera()
    {
        var process = new ProcessScanEvent(
            new CheckInScan(new ProductResolver(products, lookup, clock, new ListLogger<ProductResolver>()), units,
                TestSettings.Stock(), feed.Notifier(), clock, new ListLogger<CheckInScan>()),
            new CheckOutScan(products, units, unmatched, feed.Notifier(), clock, new ListLogger<CheckOutScan>()),
            new StoreLabelCheckInScan(storeLabels, feed.Notifier(), clock, new ListLogger<StoreLabelCheckInScan>()),
            new ListLogger<ProcessScanEvent>());
        var checkOut = new CheckOutUnit(products, units, clock, new ListLogger<CheckOutUnit>());
        return new CameraScan(process, checkOut, products, units, clock, log);
    }

    private Task<CameraScanResult> Scan(Guid id, string barcode, string role) =>
        Camera().ExecuteAsync(id, barcode, role, Gary, CancellationToken.None);

    private Guid KnownGravy()
    {
        var id = Guid.NewGuid();
        products.Lookups[Bisto] = id;
        products.Products[id] = Product.Found(id, Bisto, BistoDetails, TestScans.At, 1);
        return id;
    }

    private Unit AddUnit(Guid productId, int daysAgo, DateOnly? expiry = null)
    {
        var unit = new Unit(Guid.NewGuid(), productId, Bisto, UnitStatus.InStock, Guid.NewGuid(), "scanner-in",
            TestScans.At.AddDays(-daysAgo), null, null, null, expiry);
        units.Add(unit);
        return unit;
    }

    [Fact]
    public async Task A_check_in_goes_through_the_scanner_path_and_records_the_camera_and_the_user()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Found(BistoDetails));
        var request = Guid.NewGuid();

        var result = Assert.IsType<CameraScanResult.CheckedIn>(await Scan(request, Bisto, "in"));

        Assert.Equal(("Beef Gravy", 1), (result.DisplayName, result.Count));
        Assert.Equal(request, result.UnitId);
        var unit = Assert.Single(units.Units.Values).Unit;
        Assert.Equal(request, unit.CheckInScanId);
        Assert.Equal(CameraScanning.Device, unit.CheckInDeviceId);
        Assert.Equal(Gary.ObjectId, unit.CheckedInBy);
        Assert.Equal(ProductStatus.Known, products.Products[products.Lookups[Bisto]].Status); // looked up like any scan
        Assert.Single(lookup.Calls);
        Assert.Contains(feed.Sent, e => e is ScanIdentified);
    }

    [Fact]
    public async Task A_retry_with_the_same_request_id_does_not_double_count()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Found(BistoDetails));
        var request = Guid.NewGuid();

        await Scan(request, Bisto, "in");
        var again = Assert.IsType<CameraScanResult.CheckedIn>(await Scan(request, Bisto, "in"));

        Assert.Single(units.Units);
        Assert.Equal(1, again.Count);
    }

    [Fact]
    public async Task An_unknown_barcode_is_saved_as_a_no_match_check_in_to_name_later()
    {
        // Open Food Facts doesn't know it (the fake's default)
        var request = Guid.NewGuid();

        var result = Assert.IsType<CameraScanResult.NeedsName>(await Scan(request, Bisto, "in"));

        Assert.Equal(request, result.UnitId);
        var product = products.Products[products.Lookups[Bisto]];
        Assert.True(product.IsUnnamed);
        Assert.Single(units.InStock(product.ProductId));
        Assert.Contains(feed.Sent, e => e is ScanNeedsIdentification);
    }

    [Fact]
    public async Task A_store_label_follows_the_store_label_flow_and_has_no_unit()
    {
        var result = Assert.IsType<CameraScanResult.NeedsName>(await Scan(Guid.NewGuid(), StoreLabel, "in"));

        Assert.Null(result.UnitId);
        Assert.Empty(units.Units);
        Assert.Empty(lookup.Calls); // never looked up
        Assert.Single(storeLabels.Records);
    }

    [Fact]
    public async Task A_check_out_takes_the_soonest_expiring_unit_and_records_the_camera_and_the_user()
    {
        var gravy = KnownGravy();
        AddUnit(gravy, daysAgo: 9);
        var soonest = AddUnit(gravy, daysAgo: 1, expiry: new DateOnly(2026, 10, 20));
        var request = Guid.NewGuid();

        var result = Assert.IsType<CameraScanResult.CheckedOut>(await Scan(request, Bisto, "out"));

        Assert.Equal((soonest.UnitId, "Beef Gravy", 1), (result.UnitId, result.DisplayName, result.RemainingCount));
        var stored = units.Units[soonest.UnitId].Unit;
        Assert.Equal(UnitStatus.CheckedOut, stored.Status);
        Assert.Equal(CameraScanning.Source, stored.CheckOutSource);
        Assert.Equal(Gary.ObjectId, stored.CheckedOutBy);
        Assert.Equal(request, stored.CheckOutScanId);
    }

    [Fact]
    public async Task A_check_out_retry_gives_the_same_answer_and_takes_no_more()
    {
        var gravy = KnownGravy();
        AddUnit(gravy, daysAgo: 5);
        AddUnit(gravy, daysAgo: 3);
        var request = Guid.NewGuid();

        var first = Assert.IsType<CameraScanResult.CheckedOut>(await Scan(request, Bisto, "out"));
        var retry = Assert.IsType<CameraScanResult.CheckedOut>(await Scan(request, Bisto, "out"));

        Assert.Equal(first.UnitId, retry.UnitId);
        Assert.Single(units.InStock(gravy));
    }

    [Fact]
    public async Task Check_out_of_something_not_in_stock_records_nothing()
    {
        var gravy = KnownGravy(); // known, nothing in stock
        var unknown = "4006381333931";

        Assert.IsType<CameraScanResult.NotInStock>(await Scan(Guid.NewGuid(), Bisto, "out"));
        Assert.IsType<CameraScanResult.NotInStock>(await Scan(Guid.NewGuid(), unknown, "out")); // never seen

        Assert.Empty(unmatched.Records); // unlike a scanner's unmatched check-out
        Assert.Empty(units.Units);
        Assert.Empty(feed.Sent);
        Assert.NotEqual(Guid.Empty, gravy);
    }

    [Fact]
    public async Task A_unit_taken_by_a_scanner_at_the_same_moment_makes_it_take_the_next_one()
    {
        var gravy = KnownGravy();
        var first = AddUnit(gravy, daysAgo: 5);
        var second = AddUnit(gravy, daysAgo: 3);
        units.StolenBeforeReplace.Add(first.UnitId);

        var result = Assert.IsType<CameraScanResult.CheckedOut>(await Scan(Guid.NewGuid(), Bisto, "out"));

        Assert.Equal(second.UnitId, result.UnitId);
    }

    [Theory]
    [InlineData("12345", "in")]
    [InlineData("5000354922832", "in")] // wrong check digit
    [InlineData("5000354922831", "sideways")]
    [InlineData("5000354922831", null)]
    [InlineData(null, "in")]
    public async Task Bad_input_is_refused_and_changes_nothing(string? barcode, string? role)
    {
        Assert.IsType<CameraScanResult.Invalid>(await Camera().ExecuteAsync(Guid.NewGuid(), barcode, role, Gary, CancellationToken.None));
        Assert.IsType<CameraScanResult.Invalid>(await Camera().ExecuteAsync(Guid.Empty, Bisto, "in", Gary, CancellationToken.None));
        Assert.Empty(units.Units);
        Assert.Empty(lookup.Calls);
    }

    [Fact]
    public async Task The_log_holds_the_role_and_outcome_only()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Found(BistoDetails));
        await Scan(Guid.NewGuid(), Bisto, "in");
        await Scan(Guid.NewGuid(), "4006381333931", "out");

        Assert.Equal(["Camera scan (in): checkedIn.", "Camera scan (out): notInStock."], log.At(LogLevel.Information));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("Gravy", StringComparison.OrdinalIgnoreCase) || e.Message.Contains(Bisto));
    }
}
