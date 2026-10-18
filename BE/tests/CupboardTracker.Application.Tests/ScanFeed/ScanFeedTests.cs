using CupboardTracker.Application.Adding;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.ScanFeed;

internal sealed class FakeScanFeedQueries(FakeProducts products, FakeUnits units, FakeUnmatched unmatched) : IScanFeedQueries
{
    public List<ScanEvent> Scans { get; } = [];

    public Task<IReadOnlyList<ScanEvent>> GetScansSinceAsync(DateTimeOffset since, int max, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ScanEvent>>(Scans.Where(s => s.ReceivedAt >= since).OrderBy(s => s.ReceivedAt).Take(max).ToList());

    public Task<IReadOnlyList<Unit>> GetUnitsForScansAsync(IReadOnlyCollection<Guid> scanIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Unit>>(units.Units.Values.Select(v => v.Unit)
            .Where(u => scanIds.Contains(u.UnitId) || (u.CheckOutScanId is { } id && scanIds.Contains(id))).ToList());

    public Task<IReadOnlyList<UnmatchedCheckOut>> GetUnmatchedAsync(IReadOnlyCollection<Guid> scanIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UnmatchedCheckOut>>(unmatched.Records.Values.Where(r => scanIds.Contains(r.ScanId)).ToList());

    public Task<IReadOnlyList<Product>> GetUnnamedProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Product>>(products.Products.Values.Where(p => p.IsUnnamed).ToList());
}

/// <summary>The live scan feed (#54): events from processing and naming, best-effort, catch-up and dismissal.</summary>
public sealed class ScanFeedTests
{
    private const string Gravy = "5000354922831";
    private const string Mystery = "5010012345674";
    private static readonly ProductDetails GravyDetails = new("Beef Gravy", "Bisto", "230g", null, []);

    private readonly FixedClock clock = new(TestScans.At);
    private readonly FakeLookup lookup = new();
    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FakeUnmatched unmatched = new();
    private readonly FakeStoreLabels storeLabels = new();
    private readonly FakeFeed feed = new();
    private readonly ListLogger<ScanFeedNotifier> feedLog = new();
    private readonly FakeScanFeedQueries queries;

    public ScanFeedTests() => queries = new FakeScanFeedQueries(products, units, unmatched);

    private ProcessScanEvent Processor() => new(
        new CheckInScan(new ProductResolver(products, lookup, clock, new ListLogger<ProductResolver>()), units,
            TestSettings.Stock(), feed.Notifier(feedLog), clock, new ListLogger<CheckInScan>()),
        new CheckOutScan(products, units, unmatched, feed.Notifier(feedLog), clock, new ListLogger<CheckOutScan>()),
        new StoreLabelCheckInScan(storeLabels, feed.Notifier(feedLog), clock, new ListLogger<StoreLabelCheckInScan>()),
        new ListLogger<ProcessScanEvent>());

    private async Task<ScanEvent> Scan(string barcode, Role role, DateTimeOffset? at = null)
    {
        var scan = TestScans.Make(barcode, role, at: at ?? clock.Now);
        queries.Scans.Add(scan);
        await Processor().ExecuteAsync(scan, CancellationToken.None);
        return scan;
    }

    private GetScanFeed Catchup() => new(queries, products, units, storeLabels, clock);

    // ---- Events from processing

    [Fact]
    public async Task A_known_check_in_is_identified_with_the_product()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Found(GravyDetails));
        var scan = await Scan(Gravy, Role.In);

        var sent = Assert.IsType<ScanIdentified>(Assert.Single(feed.Sent));
        Assert.Equal((scan.ScanId, ScanOutcome.CheckedIn, "Beef Gravy", "Bisto", "230g", scan.ScanId),
            (sent.ScanId, sent.Outcome, sent.DisplayName, sent.Brand, sent.Size, sent.UnitId));
    }

    [Fact]
    public async Task An_unknown_check_in_needs_identification()
    {
        var scan = await Scan(Mystery, Role.In);

        var sent = Assert.IsType<ScanNeedsIdentification>(Assert.Single(feed.Sent));
        Assert.Equal((scan.ScanId, Mystery, ProductStatus.NoMatch, "Unknown item (5010012345674)"),
            (sent.ScanId, sent.Barcode, sent.ProductStatus, sent.DisplayName));
    }

    [Fact]
    public async Task A_check_out_is_identified_and_nothing_in_stock_is_unmatched()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Found(GravyDetails));
        var checkIn = await Scan(Gravy, Role.In);
        var checkOut = await Scan(Gravy, Role.Out);
        var none = await Scan(Gravy, Role.Out);
        var never = await Scan(Mystery, Role.Out);

        var outEvent = Assert.IsType<ScanIdentified>(feed.Sent[1]);
        Assert.Equal((checkOut.ScanId, ScanOutcome.CheckedOut, "Beef Gravy", checkIn.ScanId),
            (outEvent.ScanId, outEvent.Outcome, outEvent.DisplayName, outEvent.UnitId));
        var known = Assert.IsType<CheckOutUnmatched>(feed.Sent[2]);
        Assert.Equal((none.ScanId, "Beef Gravy"), (known.ScanId, known.DisplayName));
        var unknown = Assert.IsType<CheckOutUnmatched>(feed.Sent[3]);
        Assert.Equal((never.ScanId, (Guid?)null, (string?)null), (unknown.ScanId, unknown.ProductId, unknown.DisplayName));
    }

    [Fact]
    public async Task A_store_label_check_in_asks_what_it_is_and_a_replay_asks_again_without_a_second_record()
    {
        var scan = await Scan("2012345678903", Role.In);
        await Processor().ExecuteAsync(scan, CancellationToken.None);

        Assert.Equal(2, feed.Sent.Count);
        Assert.All(feed.Sent, e => Assert.Equal((scan.ScanId, "2012345678903"), (((StoreLabelScanned)e).ScanId, ((StoreLabelScanned)e).Barcode)));
        Assert.Single(storeLabels.Records);
        Assert.Empty(units.Units);
        Assert.Empty(products.Products);
        Assert.Empty(lookup.Calls);
    }

    [Fact]
    public async Task A_store_label_check_out_is_unmatched()
    {
        var scan = await Scan("2012345678903", Role.Out);

        Assert.Equal(scan.ScanId, Assert.IsType<CheckOutUnmatched>(Assert.Single(feed.Sent)).ScanId);
        Assert.True(unmatched.Records.ContainsKey(scan.ScanId));
        Assert.Empty(lookup.Calls);
    }

    [Fact]
    public async Task Store_label_toasts_survive_a_reload_until_assigned_or_dismissed()
    {
        var scan = await Scan("2012345678903", Role.In, clock.Now.AddHours(-3)); // outside the window
        Assert.Equal(scan.ScanId, Assert.IsType<StoreLabelScanned>(Assert.Single((await Catchup().ExecuteAsync(null, CancellationToken.None)).Events)).ScanId);

        await new DismissStoreLabelFromFeed(storeLabels, feed.Notifier(), clock, new ListLogger<DismissStoreLabelFromFeed>())
            .ExecuteAsync(scan.ScanId, new CurrentUser(Guid.NewGuid(), "G"), CancellationToken.None);

        Assert.Empty((await Catchup().ExecuteAsync(null, CancellationToken.None)).Events);
        Assert.Equal(scan.ScanId, Assert.IsType<FeedDismissed>(feed.Sent[^1]).ScanId);
        Assert.False(storeLabels.Records[scan.ScanId].CheckIn.IsResolved); // still on the scanners screen
    }

    [Fact]
    public async Task A_feed_failure_never_fails_processing_and_is_a_warning()
    {
        feed.Fail = true;
        lookup.Results.Enqueue(new ProductLookupResult.Found(GravyDetails));

        await Scan(Gravy, Role.In);
        await Scan(Gravy, Role.Out);

        Assert.Single(units.Units);
        Assert.Equal(UnitStatus.CheckedOut, units.Units.Values.Single().Unit.Status);
        Assert.Equal(2, feedLog.At(LogLevel.Warning).Count());
        Assert.Empty(feedLog.At(LogLevel.Error));
    }

    [Fact]
    public async Task Naming_tells_the_feed_and_a_failure_doesnt_stop_it()
    {
        var scan = await Scan(Mystery, Role.In);
        var productId = units.Units[scan.ScanId].Unit.ProductId;
        feed.Sent.Clear();

        await new NameProduct(products, feed.Notifier(), clock, new ListLogger<NameProduct>())
            .ExecuteAsync(productId, new NameProductCommand("Mustard", null, null), new CurrentUser(Guid.NewGuid(), "Gary"), CancellationToken.None);

        Assert.Equal(new ProductNamed(productId, "Mustard", clock.Now), Assert.Single(feed.Sent));

        var other = await Scan("5010012345681", Role.In);
        feed.Fail = true;
        var result = await new NameProduct(products, feed.Notifier(), clock, new ListLogger<NameProduct>())
            .ExecuteAsync(units.Units[other.ScanId].Unit.ProductId, new NameProductCommand("Jam", null, null),
                new CurrentUser(Guid.NewGuid(), "Gary"), CancellationToken.None);
        Assert.IsType<NameProductResult.Named>(result);
    }

    // ---- Dismissal

    [Fact]
    public async Task Dismissing_records_the_time_tells_every_app_and_changes_nothing_else()
    {
        var scan = await Scan(Mystery, Role.In);
        var productId = units.Units[scan.ScanId].Unit.ProductId;
        var before = products.Products[productId];
        clock.Now = clock.Now.AddMinutes(1);
        feed.Sent.Clear();

        var result = await new DismissFeedItem(products, feed.Notifier(), clock, new ListLogger<DismissFeedItem>())
            .ExecuteAsync(productId, new CurrentUser(Guid.NewGuid(), "Gary"), CancellationToken.None);

        Assert.Equal(new DismissFeedItemResult.Dismissed(productId, clock.Now), result);
        Assert.Equal(new FeedDismissed(productId, clock.Now), Assert.Single(feed.Sent));
        Assert.Equal(before with { FeedDismissedAt = clock.Now }, products.Products[productId]);
        Assert.Equal(UnitStatus.InStock, units.Units[scan.ScanId].Unit.Status);
    }

    [Fact]
    public async Task Dismissing_an_unknown_product_is_not_found_and_a_feed_failure_still_dismisses()
    {
        var dismiss = new DismissFeedItem(products, feed.Notifier(), clock, new ListLogger<DismissFeedItem>());
        Assert.IsType<DismissFeedItemResult.NotFound>(await dismiss.ExecuteAsync(Guid.NewGuid(), new CurrentUser(Guid.NewGuid(), "G"), CancellationToken.None));

        var scan = await Scan(Mystery, Role.In);
        feed.Fail = true;
        var productId = units.Units[scan.ScanId].Unit.ProductId;
        Assert.IsType<DismissFeedItemResult.Dismissed>(await dismiss.ExecuteAsync(productId, new CurrentUser(Guid.NewGuid(), "G"), CancellationToken.None));
        Assert.NotNull(products.Products[productId].FeedDismissedAt);
    }

    // ---- Catch-up

    [Fact]
    public async Task Catch_up_reports_each_recent_scan_with_its_outcome_now()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Found(GravyDetails));
        var old = await Scan(Gravy, Role.In, clock.Now.AddMinutes(-30));
        var checkIn = await Scan(Gravy, Role.In);
        var checkOut = await Scan(Gravy, Role.Out);
        var notInStock = await Scan(Mystery, Role.Out);
        var reading = TestScans.Make("5000354922848", Role.In, at: clock.Now); // stored, not processed yet
        queries.Scans.Add(reading);
        var label = await Scan("2012345678903", Role.In);

        var view = await Catchup().ExecuteAsync(clock.Now.AddMinutes(-5), CancellationToken.None);

        Assert.DoesNotContain(view.Events, e => e is ScanEventBase s && s.ScanId == old.ScanId);
        var byScan = view.Events.OfType<ScanEventBase>().ToDictionary(e => e.ScanId);
        Assert.Equal(ScanOutcome.CheckedIn, Assert.IsType<ScanIdentified>(byScan[checkIn.ScanId]).Outcome);
        Assert.Equal(ScanOutcome.CheckedOut, Assert.IsType<ScanIdentified>(byScan[checkOut.ScanId]).Outcome);
        Assert.IsType<CheckOutUnmatched>(byScan[notInStock.ScanId]);
        Assert.IsType<ScanReceived>(byScan[reading.ScanId]);
        Assert.IsType<StoreLabelScanned>(byScan[label.ScanId]);
    }

    [Fact]
    public async Task Amber_toasts_survive_a_reload_until_named_or_dismissed_and_come_back_on_a_newer_scan()
    {
        var first = await Scan(Mystery, Role.In, clock.Now.AddHours(-2)); // outside the window
        var productId = units.Units[first.ScanId].Unit.ProductId;

        var waiting = Assert.IsType<ScanNeedsIdentification>(Assert.Single((await Catchup().ExecuteAsync(null, CancellationToken.None)).Events));
        Assert.Equal((productId, first.ScanId), (waiting.ProductId, waiting.ScanId));

        await new DismissFeedItem(products, feed.Notifier(), clock, new ListLogger<DismissFeedItem>())
            .ExecuteAsync(productId, new CurrentUser(Guid.NewGuid(), "G"), CancellationToken.None);
        Assert.Empty((await Catchup().ExecuteAsync(null, CancellationToken.None)).Events);

        clock.Now = clock.Now.AddMinutes(5);
        var again = await Scan(Mystery, Role.In);
        var back = (await Catchup().ExecuteAsync(null, CancellationToken.None)).Events;
        Assert.Equal(again.ScanId, Assert.IsType<ScanNeedsIdentification>(Assert.Single(back)).ScanId); // one toast per product

        await new NameProduct(products, feed.Notifier(), clock, new ListLogger<NameProduct>())
            .ExecuteAsync(productId, new NameProductCommand("Mustard", null, null), new CurrentUser(Guid.NewGuid(), "G"), CancellationToken.None);
        var named = (await Catchup().ExecuteAsync(null, CancellationToken.None)).Events;
        Assert.Equal("Mustard", Assert.IsType<ScanIdentified>(Assert.Single(named)).DisplayName); // the recent scan, now identified
    }

    [Fact]
    public async Task A_recent_scan_of_a_dismissed_product_is_not_amber()
    {
        var scan = await Scan(Mystery, Role.In);
        clock.Now = clock.Now.AddMinutes(1);
        await new DismissFeedItem(products, feed.Notifier(), clock, new ListLogger<DismissFeedItem>())
            .ExecuteAsync(units.Units[scan.ScanId].Unit.ProductId, new CurrentUser(Guid.NewGuid(), "G"), CancellationToken.None);

        Assert.Empty((await Catchup().ExecuteAsync(clock.Now.AddMinutes(-5), CancellationToken.None)).Events);
    }

    [Fact]
    public async Task Nothing_in_stock_means_no_amber_toast()
    {
        var scan = await Scan(Mystery, Role.In, clock.Now.AddHours(-2));
        var unit = units.Units[scan.ScanId].Unit;
        units.Units[scan.ScanId] = (unit.CheckOutByUser(Guid.NewGuid(), clock.Now), 2);

        Assert.Empty((await Catchup().ExecuteAsync(null, CancellationToken.None)).Events);
    }

    [Fact]
    public async Task The_window_defaults_to_ten_minutes_and_is_capped_at_a_day()
    {
        lookup.Default = new ProductLookupResult.Found(GravyDetails);
        var elevenMinutes = await Scan(Gravy, Role.In, clock.Now.AddMinutes(-11));
        var nineMinutes = await Scan(Gravy, Role.In, clock.Now.AddMinutes(-9));
        var twoDays = await Scan(Gravy, Role.In, clock.Now.AddDays(-2));

        var byDefault = (await Catchup().ExecuteAsync(null, CancellationToken.None)).Events.OfType<ScanEventBase>().Select(e => e.ScanId);
        Assert.Equal([nineMinutes.ScanId], byDefault);
        var capped = (await Catchup().ExecuteAsync(clock.Now.AddDays(-30), CancellationToken.None)).Events.OfType<ScanEventBase>().Select(e => e.ScanId);
        Assert.Equal([elevenMinutes.ScanId, nineMinutes.ScanId], capped);
        Assert.DoesNotContain(twoDays.ScanId, capped);
    }
}
