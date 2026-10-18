using CupboardTracker.Application.Hubs;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Scanners;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.Hubs;
using CupboardTracker.Application.Tests.ScanFeed;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Tests.Scanners;

internal sealed class FakeScannersQueries(FakeUnits units, FakeUnmatched unmatched) : IScannersQueries
{
    public List<Device> Devices { get; } = [];

    public Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Device>>(Devices);

    public Task<IReadOnlyList<UnmatchedCheckOut>> GetUndismissedUnmatchedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UnmatchedCheckOut>>(unmatched.Records.Values.Where(r => !r.IsDismissed).ToList());

    public Task<IReadOnlyList<Unit>> GetCheckedOutSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Unit>>(units.Units.Values.Select(v => v.Unit)
            .Where(u => u.Status == UnitStatus.CheckedOut && u.CheckedOutAt >= since).ToList());
}

/// <summary>The scanners screen (#55): presence, health, and the three lists.</summary>
public sealed class ScannersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.NewGuid(), "Gary");

    private readonly FakeHubs hubs = new();
    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FakeUnmatched unmatched = new();
    private readonly FixedClock clock = new(Now);
    private readonly FakeScannersQueries queries;
    private readonly FakeScanFeedQueries feedQueries;

    public ScannersTests()
    {
        queries = new FakeScannersQueries(units, unmatched);
        feedQueries = new FakeScanFeedQueries(products, units, unmatched);
    }

    private GetScanners UseCase() => new(hubs, queries, feedQueries, units, products,
        new OfflineAlertSettings(TimeSpan.FromMinutes(20), TestSettings.London),
        new ScannersSettings(TimeSpan.FromSeconds(120)), clock);

    private RecordHeartbeat Heartbeat() => new(hubs, clock, new ListLogger<RecordHeartbeat>());

    private static RecordHeartbeatCommand Beat(params (string Id, bool Present)[] scanners) =>
        new("cupboard-pi", "2026-10-18T08:00:00Z", "abc1234", 60, [],
            scanners.Length == 0 ? null : [.. scanners.Select(s => new ScannerPresenceCommand(s.Id, s.Present))]);

    // ---- Presence

    [Fact]
    public void Presence_keeps_its_since_until_it_changes()
    {
        var t1 = Now;
        var t2 = Now.AddMinutes(2);
        var t3 = Now.AddMinutes(4);
        var first = Hub.NextScannerStates([], [new("scanner-in", true), new("scanner-out", true)], t1);
        var same = Hub.NextScannerStates(first, [new("scanner-in", true), new("scanner-out", true)], t2);
        var unplugged = Hub.NextScannerStates(same, [new("scanner-in", true), new("scanner-out", false)], t3);

        Assert.Equal(first, same);
        Assert.Equal([new ScannerState("scanner-in", true, t1), new ScannerState("scanner-out", false, t3)], unplugged);
        Assert.Same(unplugged, Hub.NextScannerStates(unplugged, null, t3.AddMinutes(2))); // no list: no change
        // Devices a heartbeat doesn't mention keep their last state.
        Assert.Equal(unplugged, Hub.NextScannerStates(unplugged, [new("scanner-in", true)], t3.AddMinutes(2)));
    }

    [Fact]
    public async Task Heartbeats_record_presentSince_and_absentSince_and_without_a_list_presence_is_unknown()
    {
        queries.Devices.AddRange([new("scanner-out", Role.Out, Now, null), new("scanner-in", Role.In, Now, Now.AddHours(-14))]);

        await Heartbeat().ExecuteAsync(Beat(), CancellationToken.None);
        var unknown = await UseCase().ExecuteAsync(CancellationToken.None);
        Assert.All(unknown.Devices, d => Assert.Null(d.Present));
        Assert.Equal([Role.In, Role.Out], unknown.Devices.Select(d => d.Role)); // check-in first

        await Heartbeat().ExecuteAsync(Beat(("scanner-in", true), ("scanner-out", true)), CancellationToken.None);
        clock.Now = Now.AddMinutes(30);
        await Heartbeat().ExecuteAsync(Beat(("scanner-in", true), ("scanner-out", false)), CancellationToken.None);
        clock.Now = Now.AddMinutes(32);

        var view = await UseCase().ExecuteAsync(CancellationToken.None);
        var checkIn = view.Devices[0];
        var checkOut = view.Devices[1];
        Assert.Equal((true, Now, (DateTimeOffset?)null, Now.AddHours(-14)), (checkIn.Present!.Value, checkIn.PresentSince!.Value, checkIn.AbsentSince, checkIn.LastScanAt!.Value));
        Assert.Equal((false, (DateTimeOffset?)null, Now.AddMinutes(30)), (checkOut.Present!.Value, checkOut.PresentSince, checkOut.AbsentSince!.Value));
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public async Task The_hub_is_online_unless_silent_for_longer_than_the_offline_alert_threshold(int minutesAgo, bool online)
    {
        await Heartbeat().ExecuteAsync(Beat(), CancellationToken.None);
        clock.Now = Now.AddMinutes(minutesAgo);

        var hub = (await UseCase().ExecuteAsync(CancellationToken.None)).Hub!;

        Assert.Equal((online, Now, 120, 20), (hub.Online, hub.LastSeenAt, (int)hub.HeartbeatInterval.TotalSeconds, (int)hub.OfflineThreshold.TotalMinutes));
    }

    [Fact]
    public async Task No_hub_yet()
    {
        Assert.Null((await UseCase().ExecuteAsync(CancellationToken.None)).Hub);
    }

    // ---- Lists

    private Guid Unknown(string barcode, ProductStatus status = ProductStatus.NoMatch, DateTimeOffset? dismissed = null)
    {
        var id = Guid.NewGuid();
        products.Products[id] = (status == ProductStatus.NoMatch ? Product.NotFound(id, barcode, Now, 1) : Product.Failed(id, barcode, Now, 1, null))
            with { FeedDismissedAt = dismissed };
        return id;
    }

    private Guid Known(string name)
    {
        var id = Guid.NewGuid();
        products.Products[id] = Product.Found(id, "5000354922831", new ProductDetails(name, null, null, null, []), Now, 1);
        return id;
    }

    private Unit AddUnit(Guid productId, DateTimeOffset checkedInAt, string? barcode = "5010012345674")
    {
        var id = Guid.NewGuid();
        var unit = new Unit(id, productId, barcode, UnitStatus.InStock, id, "scanner-in", checkedInAt, null, null, null, null);
        units.Add(unit);
        return unit;
    }

    [Fact]
    public async Task No_match_check_ins_need_stock_include_feed_dismissals_and_are_newest_first()
    {
        var older = Unknown("5010012345674");
        AddUnit(older, Now.AddDays(-2));
        var newer = Unknown("5010012345681", ProductStatus.LookupFailed, dismissed: Now); // dismissed from the feed: still here
        AddUnit(newer, Now.AddHours(-1), "5010012345681");
        AddUnit(newer, Now.AddHours(-3), "5010012345681");
        var gone = Unknown("5010012345698");
        var unit = AddUnit(gone, Now.AddHours(-2));
        units.Units[unit.UnitId] = (unit.CheckOutByUser(Gary.ObjectId, Now), 2); // nothing in stock any more
        AddUnit(Known("Beef Gravy"), Now);

        var list = (await UseCase().ExecuteAsync(CancellationToken.None)).NoMatchCheckIns;

        Assert.Equal([newer, older], list.Select(n => n.ProductId));
        Assert.Equal(("5010012345681", ProductStatus.LookupFailed, Now.AddHours(-1), 2), (list[0].Barcode, list[0].Status, list[0].ScannedAt, list[0].InStockCount));
    }

    [Fact]
    public async Task Unmatched_check_outs_are_undismissed_only_newest_first_with_known_names()
    {
        var gravy = Known("Beef Gravy");
        var mystery = Unknown("5010012345674");
        unmatched.Records[Guid.NewGuid()] = new(Guid.NewGuid(), "20055363", null, "scanner-out", Now.AddDays(-9), Now);
        var recentId = Guid.NewGuid();
        unmatched.Records[recentId] = new(recentId, "5000354922831", gravy, "scanner-out", Now.AddHours(-1), Now);
        var unknownId = Guid.NewGuid();
        unmatched.Records[unknownId] = new(unknownId, "5010012345674", mystery, "scanner-out", Now.AddHours(-2), Now);
        var dismissedId = Guid.NewGuid();
        unmatched.Records[dismissedId] = new(dismissedId, "1", null, "scanner-out", Now, Now, Now, Gary.ObjectId);

        var list = (await UseCase().ExecuteAsync(CancellationToken.None)).UnmatchedCheckOuts;

        Assert.Equal(3, list.Count); // a week-old one is still here until dismissed
        Assert.Equal((recentId, "Beef Gravy"), (list[0].ScanId, list[0].DisplayName));
        Assert.Equal((unknownId, (string?)null), (list[1].ScanId, list[1].DisplayName)); // shown as "Unknown item <barcode>"
        Assert.DoesNotContain(list, u => u.ScanId == dismissedId);
    }

    [Fact]
    public async Task Recently_checked_out_is_the_last_48_hours_from_a_scanner_or_the_app_newest_first()
    {
        var gravy = Known("Beef Gravy");
        var scanned = AddUnit(gravy, Now.AddDays(-5), "5000354922831");
        units.Units[scanned.UnitId] = (scanned.CheckOut(Guid.NewGuid(), Now.AddHours(-47), "scanner"), 2);
        var inApp = AddUnit(gravy, Now.AddDays(-5), "5000354922831");
        units.Units[inApp.UnitId] = (inApp.CheckOutByUser(Gary.ObjectId, Now.AddHours(-1)), 2);
        var old = AddUnit(gravy, Now.AddDays(-5), "5000354922831");
        units.Units[old.UnitId] = (old.CheckOut(Guid.NewGuid(), Now.AddHours(-49), "scanner"), 2);
        AddUnit(gravy, Now); // in stock

        var list = (await UseCase().ExecuteAsync(CancellationToken.None)).RecentlyCheckedOut;

        Assert.Equal([inApp.UnitId, scanned.UnitId], list.Select(r => r.UnitId));
        Assert.Equal([Unit.AppSource, "scanner"], list.Select(r => r.Source));
        Assert.All(list, r => Assert.Equal("Beef Gravy", r.DisplayName));
    }

    [Fact]
    public async Task Undo_puts_a_scanner_check_out_back_in_stock()
    {
        var unit = AddUnit(Known("Beef Gravy"), Now.AddDays(-1), "5000354922831");
        units.Units[unit.UnitId] = (unit.CheckOut(Guid.NewGuid(), Now.AddHours(-1), "scanner"), 2);

        var result = await new UndoCheckOut(units, new ListLogger<UndoCheckOut>()).ExecuteAsync(unit.UnitId, CancellationToken.None);

        Assert.IsType<UndoCheckOutResult.InStock>(result);
        Assert.Equal(unit, units.Units[unit.UnitId].Unit);
    }

    [Fact]
    public async Task Dismissing_an_unmatched_check_out_is_idempotent_and_keeps_the_first()
    {
        var id = Guid.NewGuid();
        unmatched.Records[id] = new(id, "20055363", null, "scanner-out", Now, Now);
        var dismiss = new DismissUnmatchedCheckOut(unmatched, clock, new ListLogger<DismissUnmatchedCheckOut>());

        Assert.IsType<DismissUnmatchedCheckOutResult.Dismissed>(await dismiss.ExecuteAsync(id, Gary, CancellationToken.None));
        clock.Now = Now.AddMinutes(5);
        Assert.IsType<DismissUnmatchedCheckOutResult.Dismissed>(await dismiss.ExecuteAsync(id, new CurrentUser(Guid.NewGuid(), "x"), CancellationToken.None));

        Assert.Equal((Now, Gary.ObjectId), (unmatched.Records[id].DismissedAt!.Value, unmatched.Records[id].DismissedBy!.Value));
        Assert.IsType<DismissUnmatchedCheckOutResult.NotFound>(await dismiss.ExecuteAsync(Guid.NewGuid(), Gary, CancellationToken.None));
        Assert.Empty((await UseCase().ExecuteAsync(CancellationToken.None)).UnmatchedCheckOuts);
    }
}
