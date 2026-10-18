using CupboardTracker.Application.Products;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Stock;

public sealed class StockTests
{
    private const string Bisto = "5000354922831";
    private static readonly ProductDetails BistoDetails = new("Beef Gravy", "Bisto", "230g", null, ["en:gravy"]);

    private readonly FixedClock clock = new(TestScans.At);
    private readonly FakeLookup lookup = new();
    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FakeUnmatched unmatched = new();
    private readonly ListLogger<ProductResolver> resolverLog = new();
    private readonly ListLogger<RetryFailedLookups> retryLog = new();

    private ProductResolver Resolver() => new(products, lookup, clock, resolverLog);

    private readonly FakeFeed feed = new();

    private ProcessScanEvent Processor() => new(
        new CheckInScan(Resolver(), units, TestSettings.Stock(), feed.Notifier(), clock, new ListLogger<CheckInScan>()),
        new CheckOutScan(products, units, unmatched, feed.Notifier(), clock, new ListLogger<CheckOutScan>()),
        feed.Notifier(), clock, new ListLogger<ProcessScanEvent>());

    private Task Process(ScanEvent scan) => Processor().ExecuteAsync(scan, CancellationToken.None);

    // ---- Check-in (#45)

    [Fact]
    public async Task A_new_barcode_found_on_open_food_facts_creates_a_known_product_its_lookup_and_a_unit()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Found(BistoDetails));
        var scan = TestScans.Make(Bisto, Role.In);

        await Process(scan);

        var productId = products.Lookups[Bisto];
        var product = products.Products[productId];
        Assert.Equal(ProductStatus.Known, product.Status);
        Assert.Equal(ProductSource.OpenFoodFacts, product.Source);
        Assert.Equal("Beef Gravy", product.DisplayName);
        Assert.Equal([Bisto], product.Barcodes);
        var unit = Assert.Single(units.InStock(productId));
        Assert.Equal(scan.ScanId, unit.UnitId);
        Assert.Equal(scan.ScannedAt, unit.CheckedInAt); // the bought date
        Assert.Equal("scanner-in", unit.CheckInDeviceId);
    }

    [Fact]
    public async Task A_new_product_has_no_estimate_until_the_shelf_life_timer_runs()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Found(BistoDetails));
        await Process(TestScans.Make(Bisto, Role.In));

        Assert.Null(Assert.Single(units.Units.Values).Unit.EstimatedExpiryDate);
        Assert.True(products.Products[products.Lookups[Bisto]].NeedsShelfLife);
    }

    [Fact]
    public async Task A_check_in_of_a_product_with_a_shelf_life_gets_its_estimated_expiry_date_at_once()
    {
        var id = Guid.NewGuid();
        products.Lookups[Bisto] = id;
        products.Products[id] = Product.Found(id, Bisto, BistoDetails, TestScans.At, 1)
            .WithShelfLife(new ShelfLife(365, Storage.Cupboard, 0.65, false, "jev", TestScans.At));
        // 23:30 UTC on 24 Oct is 00:30 BST on 25 Oct: bought on the 25th.
        await Process(TestScans.Make(Bisto, Role.In, at: new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero)));

        Assert.Equal(new DateOnly(2027, 10, 25), Assert.Single(units.Units.Values).Unit.EstimatedExpiryDate);
        Assert.Empty(lookup.Calls);
    }

    [Fact]
    public async Task Two_scans_of_the_same_barcode_make_two_units_and_one_lookup()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Found(BistoDetails));
        await Process(TestScans.Make(Bisto, Role.In));
        await Process(TestScans.Make(Bisto, Role.In));

        Assert.Single(lookup.Calls);
        Assert.Equal(2, units.InStock(products.Lookups[Bisto]).Count());
    }

    [Fact]
    public async Task A_replayed_check_in_makes_one_unit()
    {
        var scan = TestScans.Make(Bisto, Role.In);
        await Process(scan);
        await Process(scan);
        Assert.Single(units.Units);
    }

    [Fact]
    public async Task Not_found_becomes_a_final_no_match_named_unknown_item()
    {
        lookup.Results.Enqueue(new ProductLookupResult.NotFound());
        await Process(TestScans.Make("20590093", Role.In));
        await Process(TestScans.Make("20590093", Role.In));

        var product = products.Products[products.Lookups["20590093"]];
        Assert.Equal(ProductStatus.NoMatch, product.Status);
        Assert.Equal("Unknown item (20590093)", product.DisplayName);
        Assert.Single(lookup.Calls); // no-matches are never re-fetched
    }

    [Fact]
    public async Task A_transient_failure_still_creates_the_unit_as_lookup_failed_with_a_warning()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Transient("HTTP 429"));
        await Process(TestScans.Make(Bisto, Role.In));

        var product = products.Products[products.Lookups[Bisto]];
        Assert.Equal(ProductStatus.LookupFailed, product.Status);
        Assert.Equal("Unknown item (5000354922831)", product.DisplayName);
        Assert.Equal(TestScans.At, product.LookupFailedSince);
        Assert.Single(units.Units);
        Assert.Single(resolverLog.At(LogLevel.Warning));
    }

    [Fact]
    public async Task The_next_check_in_retries_a_lookup_failed_product_and_upgrades_it()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Transient("HTTP 503"));
        lookup.Results.Enqueue(new ProductLookupResult.Found(BistoDetails));
        await Process(TestScans.Make(Bisto, Role.In));
        await Process(TestScans.Make(Bisto, Role.In));

        var product = products.Products[products.Lookups[Bisto]];
        Assert.Equal(ProductStatus.Known, product.Status);
        Assert.Equal("Beef Gravy", product.DisplayName);
        Assert.Equal(2, product.LookupAttempts);
        Assert.Equal(2, units.Units.Count); // both units point at the same (now known) product
    }

    [Fact]
    public async Task Known_and_manually_named_products_are_never_overwritten_or_looked_up()
    {
        var id = Guid.NewGuid();
        products.Lookups[Bisto] = id;
        products.Products[id] = new Product(id, ProductStatus.Known, ProductSource.Manual, [Bisto], "Gary's gravy",
            null, null, 1, null, null);

        await Process(TestScans.Make(Bisto, Role.In));

        Assert.Empty(lookup.Calls);
        Assert.Equal("Gary's gravy", products.Products[id].DisplayName);
    }

    [Fact]
    public async Task Concurrent_scans_of_a_new_barcode_share_one_product()
    {
        // Another scan claims the barcode (and stores its product) just before ours does.
        var winner = Guid.NewGuid();
        products.BeforeWrite = () =>
        {
            products.BeforeWrite = null;
            products.Lookups.TryAdd(Bisto, winner);
            products.Products.TryAdd(winner, Product.Found(winner, Bisto, BistoDetails, TestScans.At, 1));
        };

        await Process(TestScans.Make(Bisto, Role.In));

        Assert.Single(products.Products);
        Assert.Equal(winner, Assert.Single(units.Units.Values).Unit.ProductId);
    }

    [Fact]
    public async Task Store_labels_are_skipped()
    {
        await Process(TestScans.Make("2012345678903", Role.In));
        Assert.Empty(units.Units);
        Assert.Empty(lookup.Calls);
    }

    // ---- Check-out (#47)

    private Guid StockOf(string barcode, params DateTimeOffset[] checkedInAt)
    {
        var id = Guid.NewGuid();
        products.Lookups[barcode] = id;
        products.Products[id] = Product.Found(id, barcode, BistoDetails, TestScans.At, 1);
        foreach (var at in checkedInAt)
        {
            units.Add(new Unit(Guid.NewGuid(), id, barcode, UnitStatus.InStock, null, "scanner-in", at, null, null, null, null));
        }

        return id;
    }

    [Fact]
    public async Task A_check_out_takes_one_unit_the_oldest_first_and_marks_it_checked_out()
    {
        var id = StockOf(Bisto, TestScans.At.AddDays(-2), TestScans.At.AddDays(-5));
        var oldest = units.InStock(id).OrderBy(u => u.CheckedInAt).First();
        var scan = TestScans.Make(Bisto, Role.Out);

        await Process(scan);

        var taken = units.Units[oldest.UnitId].Unit;
        Assert.Equal(UnitStatus.CheckedOut, taken.Status);
        Assert.Equal(scan.ScannedAt, taken.CheckedOutAt);
        Assert.Equal(scan.ScanId, taken.CheckOutScanId);
        Assert.Equal("scanner", taken.CheckOutSource);
        Assert.Single(units.InStock(id));
    }

    [Fact]
    public async Task Repeat_check_outs_reduce_stock_one_at_a_time_then_become_unmatched()
    {
        var id = StockOf(Bisto, TestScans.At.AddDays(-2), TestScans.At.AddDays(-1));
        await Process(TestScans.Make(Bisto, Role.Out));
        Assert.Single(units.InStock(id));
        await Process(TestScans.Make(Bisto, Role.Out));
        Assert.Empty(units.InStock(id));

        var third = TestScans.Make(Bisto, Role.Out);
        await Process(third);
        var record = unmatched.Records[third.ScanId];
        Assert.Equal(id, record.ProductId); // known product, nothing in stock
        Assert.Equal(2, units.Units.Values.Count(v => v.Unit.Status == UnitStatus.CheckedOut));
    }

    [Fact]
    public async Task A_barcode_we_have_never_seen_is_an_unmatched_check_out_with_no_product()
    {
        var scan = TestScans.Make("5010029212464", Role.Out);
        await Process(scan);
        var record = unmatched.Records[scan.ScanId];
        Assert.Null(record.ProductId);
        Assert.Equal("5010029212464", record.Barcode);
        Assert.Empty(units.Units);
        Assert.Empty(lookup.Calls); // check-out never looks products up
    }

    [Fact]
    public async Task A_replayed_check_out_never_takes_a_second_unit()
    {
        var id = StockOf(Bisto, TestScans.At.AddDays(-2), TestScans.At.AddDays(-1));
        var scan = TestScans.Make(Bisto, Role.Out);
        await Process(scan);
        await Process(scan);
        Assert.Single(units.InStock(id));
    }

    [Fact]
    public async Task A_replayed_unmatched_check_out_is_recorded_once()
    {
        var scan = TestScans.Make("5010029212464", Role.Out);
        await Process(scan);
        await Process(scan);
        Assert.Single(unmatched.Records);
    }

    [Fact]
    public async Task On_an_etag_conflict_it_takes_the_next_unit_so_concurrent_check_outs_take_different_units()
    {
        var id = StockOf(Bisto, TestScans.At.AddDays(-5), TestScans.At.AddDays(-1));
        var oldest = units.InStock(id).OrderBy(u => u.CheckedInAt).First();
        var newer = units.InStock(id).OrderBy(u => u.CheckedInAt).Last();
        units.StolenBeforeReplace.Add(oldest.UnitId); // a concurrent check-out takes the oldest first
        var scan = TestScans.Make(Bisto, Role.Out);

        await Process(scan);

        Assert.Equal(scan.ScanId, units.Units[newer.UnitId].Unit.CheckOutScanId);
        Assert.NotEqual(scan.ScanId, units.Units[oldest.UnitId].Unit.CheckOutScanId);
        Assert.Empty(units.InStock(id));
    }

    // ---- Lookup-retry timer (#45)

    [Fact]
    public async Task The_timer_retries_lookup_failed_products_on_the_retry_budget_and_stops_once_resolved()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Transient("HTTP 429"));
        await Process(TestScans.Make(Bisto, Role.In));
        lookup.Results.Enqueue(new ProductLookupResult.Found(BistoDetails));
        var timer = new RetryFailedLookups(products, Resolver(), clock, retryLog);

        Assert.Equal(1, await timer.ExecuteAsync(CancellationToken.None));
        Assert.Equal(LookupBudget.Retry, lookup.Calls[^1].Budget);
        Assert.Equal(ProductStatus.Known, products.Products[products.Lookups[Bisto]].Status);

        Assert.Equal(0, await timer.ExecuteAsync(CancellationToken.None));
        Assert.Equal(2, lookup.Calls.Count); // nothing left to retry
    }

    [Fact]
    public async Task Still_failing_after_24_hours_logs_an_error()
    {
        lookup.Default = new ProductLookupResult.Transient("HTTP 503");
        await Process(TestScans.Make(Bisto, Role.In));
        var timer = new RetryFailedLookups(products, Resolver(), clock, retryLog);

        clock.Now = TestScans.At.AddHours(23);
        await timer.ExecuteAsync(CancellationToken.None);
        Assert.Empty(retryLog.At(LogLevel.Error));

        clock.Now = TestScans.At.AddHours(25);
        await timer.ExecuteAsync(CancellationToken.None);
        Assert.Single(retryLog.At(LogLevel.Error));
    }
}
