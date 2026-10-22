using CupboardTracker.Application.Push;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.Push;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Tests.Stock;

internal sealed class FakeStillGotThisPushLog : IStillGotThisPushLog
{
    public HashSet<DateOnly> Days { get; } = [];

    public Task<bool> TryClaimAsync(DateOnly day, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Days.Add(day));

    public Task ReleaseAsync(DateOnly day, CancellationToken cancellationToken)
    {
        Days.Remove(day);
        return Task.CompletedTask;
    }
}

// "Still got this?" on items at their estimate (#94).
public sealed class StillGotThisTests
{
    // 17:00 UTC on Tuesday 20 Oct 2026 is 18:00 BST.
    private static readonly DateTimeOffset TuesdaySix = new(2026, 10, 20, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 20);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FixedClock clock = new(TuesdaySix);
    private readonly FakeStockQueries queries = new();
    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly StockSettings stock = TestSettings.Stock();
    private readonly StillGotThisSettings settings = new();

    private Guid Known(string name, Storage storage = Storage.Fridge, string? generic = null)
    {
        var id = Guid.NewGuid();
        var product = Product.Found(id, "5000354922831", new ProductDetails(name, null, null, null, []), TuesdaySix, 1)
            .WithShelfLife(new ShelfLife(7, storage, 0.9, false, ShelfLife.JevSource, TuesdaySix));
        if (generic is not null)
        {
            product = product.WithManualGenericName(GenericName.Create(generic, "chilledDeli", TuesdaySix, "manual"), TuesdaySix);
        }

        products.Products[id] = product;
        return id;
    }

    private Guid Unnamed()
    {
        var id = Guid.NewGuid();
        products.Products[id] = Product.NotFound(id, "5012345678900", TuesdaySix, 1);
        return id;
    }

    private StockUnit Unit(Guid productId, int? daysFromToday, DateTimeOffset? confirmedAt = null, DateOnly? openedOn = null,
        Storage? current = null, DateTimeOffset? boughtAt = null)
    {
        var unit = new StockUnit(productId, null, boughtAt ?? new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero),
            daysFromToday is { } d ? Today.AddDays(d) : null, ExpirySource.ShelfLife, openedOn, null, null, Guid.NewGuid(), confirmedAt, current);
        queries.Units.Add(unit);
        return unit;
    }

    private bool Asks(int? daysFromToday, DateTimeOffset? confirmedAt = null, StillGotThisSettings? with = null) =>
        StillGotThisRules.Asks(daysFromToday is { } d ? Today.AddDays(d) : null, confirmedAt, Today, stock, with ?? settings);

    // ---- Which units ask

    [Fact]
    public void A_unit_asks_from_its_estimated_day_not_before_and_never_without_an_estimate()
    {
        Assert.True(Asks(0));
        Assert.True(Asks(-3));
        Assert.False(Asks(1));
        Assert.False(Asks(null));
    }

    [Fact]
    public void The_day_is_the_UK_date()
    {
        // 23:30 UTC on 19 Oct is 00:30 BST on the 20th: a unit estimated until the 20th asks.
        var lateNight = new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);
        Assert.True(StillGotThisRules.Asks(Today, null, stock.Today(lateNight), stock, settings));
    }

    [Fact]
    public void Still_here_quiets_it_until_two_days_later()
    {
        var monday = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var sunday = new DateTimeOffset(2026, 10, 18, 22, 30, 0, TimeSpan.Zero); // 23:30 BST: still Sunday in the UK

        Assert.False(Asks(-1, TuesdaySix)); // today
        Assert.False(Asks(-1, monday)); // yesterday
        Assert.True(Asks(-1, sunday)); // two days ago: asks again
    }

    [Fact]
    public void The_gap_is_a_setting()
    {
        var sunday = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        Assert.False(Asks(-1, sunday, new StillGotThisSettings(3)));
    }

    [Fact]
    public void Expired_means_the_estimate_has_passed_not_today()
    {
        Assert.False(StillGotThisRules.IsExpired(Today, null, Today, stock, settings));
        Assert.True(StillGotThisRules.IsExpired(Today.AddDays(-1), null, Today, stock, settings));
        Assert.False(StillGotThisRules.IsExpired(Today.AddDays(-1), TuesdaySix, Today, stock, settings));
    }

    // ---- On the stock list

    private GetStock Stock() => new(queries, products, stock, clock, settings);

    [Fact]
    public async Task A_product_asks_once_about_its_oldest_asking_unit_with_the_count()
    {
        var hummus = Known("Hummus");
        Unit(hummus, -2, openedOn: new DateOnly(2026, 10, 16));
        var oldest = Unit(hummus, -4);
        Unit(hummus, 0);
        Unit(hummus, 5);

        var row = (await Stock().ExecuteAsync(CancellationToken.None)).Products.Single();

        var ask = Assert.IsType<StockAsk>(row.Ask);
        Assert.Equal((oldest.UnitId, Today.AddDays(-4), 4, 3), (ask.UnitId, ask.EstimatedExpiryDate, ask.DaysOver, ask.AskingCount));
        Assert.Equal(new DateOnly(2026, 10, 14), ask.BoughtOn);
    }

    [Fact]
    public async Task Nothing_asks_before_its_day_without_an_estimate_or_while_unnamed()
    {
        Unit(Known("Coleslaw"), 1);
        Unit(Known("Flour"), null);
        Unit(Unnamed(), -3);

        var view = await Stock().ExecuteAsync(CancellationToken.None);

        Assert.All(view.Products, p => Assert.Null(p.Ask));
    }

    [Fact]
    public async Task The_aisle_counts_products_to_check_and_a_confirmed_one_shows_checked_today()
    {
        Unit(Known("Hummus"), -1);
        Unit(Known("Ham"), 0);
        Unit(Known("Coleslaw"), 0, confirmedAt: TuesdaySix.AddHours(-2));

        var view = await Stock().ExecuteAsync(CancellationToken.None);

        Assert.Equal(2, Assert.Single(view.Aisles).ToCheckCount);
        var coleslaw = view.Products.Single(p => p.DisplayName == "Coleslaw");
        Assert.Null(coleslaw.Ask);
        Assert.True(coleslaw.CheckedToday);
    }

    // ---- Still here

    private MarkStillHere StillHere() => new(units, clock, new ListLogger<MarkStillHere>());

    private Unit InStock(Guid productId)
    {
        var unit = Domain.Stock.Unit.AddedInApp(Guid.NewGuid(), productId, Gary.ObjectId, TuesdaySix.AddDays(-5), Today.AddDays(-1));
        units.Add(unit);
        return unit;
    }

    [Fact]
    public async Task Still_here_records_when_and_how_and_undo_clears_it()
    {
        var unit = InStock(Known("Hummus"));

        var saved = Assert.IsType<StillHereResult.Saved>(await StillHere().ExecuteAsync(unit.UnitId, Gary, CancellationToken.None));
        var stored = units.Units[unit.UnitId].Unit;
        Assert.Equal((TuesdaySix, ConfirmedVia.Prompt, Today.AddDays(-1)), (stored.LastConfirmedAt!.Value, stored.LastConfirmedVia!, stored.EstimatedExpiryDate!.Value));
        Assert.Equal(TuesdaySix, saved.LastConfirmedAt);

        Assert.IsType<StillHereResult.Saved>(await StillHere().ExecuteAsync(unit.UnitId, Gary, CancellationToken.None)); // again: fine

        var undone = Assert.IsType<StillHereResult.Saved>(await StillHere().UndoAsync(unit.UnitId, Gary, CancellationToken.None));
        Assert.Null(undone.LastConfirmedAt);
        Assert.Null(units.Units[unit.UnitId].Unit.LastConfirmedVia);
        Assert.IsType<StillHereResult.Saved>(await StillHere().UndoAsync(unit.UnitId, Gary, CancellationToken.None)); // again: fine
    }

    [Fact]
    public async Task Still_here_on_a_missing_or_checked_out_unit_is_refused()
    {
        var unit = InStock(Known("Hummus"));
        units.Units[unit.UnitId] = (unit.CheckOutByUser(Gary.ObjectId, TuesdaySix), 2);

        Assert.IsType<StillHereResult.NotFound>(await StillHere().ExecuteAsync(Guid.NewGuid(), Gary, CancellationToken.None));
        Assert.IsType<StillHereResult.NotInStock>(await StillHere().ExecuteAsync(unit.UnitId, Gary, CancellationToken.None));
    }

    // ---- Gone

    [Fact]
    public async Task Gone_checks_out_that_unit_as_a_stock_check_and_undo_clears_the_reason()
    {
        var hummus = Known("Hummus");
        InStock(hummus);
        var unit = InStock(hummus);

        var gone = await new CheckOutUnit(products, units, clock, new ListLogger<CheckOutUnit>())
            .ExecuteAsync(unit.UnitId, Gary, CancellationToken.None, CheckOutReasons.StockCheck);

        Assert.Equal(1, Assert.IsType<CheckOutUnitResult.CheckedOut>(gone).RemainingCount);
        var stored = units.Units[unit.UnitId].Unit;
        Assert.Equal((UnitStatus.CheckedOut, CheckOutReasons.StockCheck, Gary.ObjectId), (stored.Status, stored.CheckOutReason!, stored.CheckedOutBy!.Value));

        await new UndoCheckOut(units, new ListLogger<UndoCheckOut>()).ExecuteAsync(unit.UnitId, CancellationToken.None, Gary);
        Assert.Equal((UnitStatus.InStock, (string?)null), (units.Units[unit.UnitId].Unit.Status, units.Units[unit.UnitId].Unit.CheckOutReason));
    }

    [Fact]
    public async Task An_ordinary_check_out_has_no_reason()
    {
        var unit = InStock(Known("Hummus"));

        await new CheckOutUnit(products, units, clock, new ListLogger<CheckOutUnit>()).ExecuteAsync(unit.UnitId, Gary, CancellationToken.None);

        Assert.Null(units.Units[unit.UnitId].Unit.CheckOutReason);
    }

    // ---- The sheet

    private GetExpiredUnits Expired() => new(queries, products, stock, settings, clock);

    [Fact]
    public async Task The_sheet_lists_expired_named_units_oldest_first_with_where_they_are()
    {
        var hummus = Known("Tesco Reduced Fat Houmous", Storage.Fridge, generic: "Hummus");
        var ham = Known("Cooked ham");
        var peas = Known("Garden peas", Storage.Freezer);
        var h = Unit(hummus, -9);
        var m = Unit(ham, -4, openedOn: new DateOnly(2026, 10, 18), current: Storage.Fridge);
        Unit(ham, 0); // today: the row asks, but it isn't expired
        Unit(peas, -2, confirmedAt: TuesdaySix.AddDays(-1)); // said still here yesterday
        Unit(Unnamed(), -5);

        var list = await Expired().ExecuteAsync(CancellationToken.None);

        Assert.Equal([h.UnitId, m.UnitId], list.Select(u => u.UnitId));
        Assert.Equal(("Hummus", Storage.Fridge, 9, new DateOnly(2026, 10, 14)), (list[0].Name, list[0].Storage!.Value, list[0].DaysOver, list[0].BoughtOn));
        Assert.Equal(new DateOnly(2026, 10, 18), list[1].OpenedOn);
    }

    // ---- The push

    private readonly FakePushSubscriptions subscriptions = new();
    private readonly FakePushSender sender = new();
    private readonly FakeStillGotThisPushLog log = new();

    private SendStillGotThisPush Push() => new(Expired(), log,
        new SendPush(subscriptions, new FakePushSeen(), sender, clock, new ListLogger<SendPush>()), stock, clock,
        new ListLogger<SendStillGotThisPush>());

    private void Device(string id, bool? stillGotThis)
    {
        var preferences = new Dictionary<string, bool>();
        if (stillGotThis is { } on)
        {
            preferences[SendStillGotThisPush.Type] = on;
        }

        subscriptions.Items[(Gary.ObjectId, id)] = new PushSubscription(id, Gary.ObjectId, $"https://web.push.apple.com/{id}", "p", "a", "iPhone",
            TuesdaySix, TuesdaySix, null, preferences);
    }

    [Fact]
    public async Task At_six_on_a_Tuesday_it_pushes_opted_in_devices_once_with_the_count_and_the_sheet()
    {
        Unit(Known("Hummus"), -9);
        Unit(Known("Ham"), -4);
        Device("on", true);
        Device("default", null); // on unless turned off
        Device("off", false);

        await Push().ExecuteAsync(CancellationToken.None);
        await Push().ExecuteAsync(CancellationToken.None); // a re-run the same day

        Assert.Equal(["default", "on"], sender.Sent.Select(s => s.Subscription.Id).Order());
        var message = sender.Sent[0].Message;
        Assert.Equal(("Cupboard", "2 expired items are still in stock. Still got these?", "/?check=expired", "stillGotThis", TimeSpan.FromHours(6)),
            (message.Title, message.Body, message.Path, message.Topic, message.TimeToLive));
        Assert.Null(PushMessage.Problem(message));
    }

    [Fact]
    public void One_item_is_said_in_the_singular()
    {
        Assert.Equal("1 expired item is still in stock. Still got it?", SendStillGotThisPush.Message(1).Body);
    }

    [Fact]
    public async Task Nothing_expired_means_no_push()
    {
        Unit(Known("Ham"), 0);
        Device("on", true);

        await Push().ExecuteAsync(CancellationToken.None);

        Assert.Empty(sender.Sent);
        Assert.Empty(log.Days);
    }

    [Theory]
    [InlineData("2026-10-20T17:00:00Z", true)] // Tue 18:00 BST
    [InlineData("2026-10-20T16:00:00Z", false)] // Tue 17:00 BST
    [InlineData("2026-10-27T18:00:00Z", true)] // Tue 18:00 GMT
    [InlineData("2026-10-27T17:00:00Z", false)] // Tue 17:00 GMT
    [InlineData("2026-10-22T17:00:00Z", true)] // Thu 18:00 BST
    [InlineData("2026-11-01T18:00:00Z", true)] // Sun 18:00 GMT
    [InlineData("2026-10-21T17:00:00Z", false)] // Wed
    [InlineData("2026-10-24T17:00:00Z", false)] // Sat
    public void It_sends_only_at_six_UK_time_on_Tuesday_Thursday_and_Sunday(string at, bool sends)
    {
        Assert.Equal(sends, SendStillGotThisPush.IsSendTime(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), stock.HomeTimeZone));
    }

    [Fact]
    public async Task Outside_the_hour_it_does_nothing()
    {
        Unit(Known("Hummus"), -9);
        Device("on", true);
        clock.Now = TuesdaySix.AddHours(-1);

        Assert.Null(await Push().ExecuteAsync(CancellationToken.None));
        Assert.Empty(sender.Sent);
    }
}
