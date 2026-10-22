using CupboardTracker.Application.Adding;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Push;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Stock;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Tests.Push;

internal sealed class FakeBatches : IStockChangeBatchRepository
{
    private int version;

    public Versioned<StockChangeBatch>? Open { get; private set; }

    public List<StockChangeBatch> Closed { get; } = [];

    public bool Fail { get; set; }

    /// <summary>Runs before the next write, e.g. to simulate a concurrent change.</summary>
    public Action? BeforeWrite { get; set; }

    public Dictionary<Guid, StockChangeBatch> Sent { get; } = [];

    public Task SaveSentAsync(StockChangeBatch batch, CancellationToken cancellationToken)
    {
        Sent[batch.BatchId] = batch;
        return Task.CompletedTask;
    }

    public Task<StockChangeBatch?> GetSentAsync(Guid batchId, CancellationToken cancellationToken) =>
        Task.FromResult(Sent.GetValueOrDefault(batchId));

    public Task<Versioned<StockChangeBatch>?> GetOpenAsync(CancellationToken cancellationToken) =>
        Fail ? throw new InvalidOperationException("Cosmos is down") : Task.FromResult(Open);

    public Task<bool> TryCreateAsync(StockChangeBatch batch, CancellationToken cancellationToken)
    {
        Before();
        if (Open is not null)
        {
            return Task.FromResult(false);
        }

        Set(batch);
        return Task.FromResult(true);
    }

    public Task<bool> TryReplaceAsync(Versioned<StockChangeBatch> current, StockChangeBatch updated, CancellationToken cancellationToken)
    {
        Before();
        if (Open?.ETag != current.ETag)
        {
            return Task.FromResult(false);
        }

        Set(updated);
        return Task.FromResult(true);
    }

    public Task<bool> TryCloseAsync(Versioned<StockChangeBatch> current, CancellationToken cancellationToken)
    {
        Before();
        if (Open?.ETag != current.ETag)
        {
            return Task.FromResult(false);
        }

        Closed.Add(Open.Value);
        Open = null;
        return Task.FromResult(true);
    }

    public void Set(StockChangeBatch batch) => Open = new(batch, $"v{++version}");

    private void Before()
    {
        var action = BeforeWrite;
        BeforeWrite = null;
        action?.Invoke();
    }
}

internal sealed class FakeStockPushScheduler : IStockPushScheduler
{
    public List<(Guid BatchId, TimeSpan Delay)> Scheduled { get; } = [];

    public Task ScheduleAsync(Guid batchId, TimeSpan delay, CancellationToken cancellationToken)
    {
        Scheduled.Add((batchId, delay));
        return Task.CompletedTask;
    }
}

public sealed class StockChangePushTests
{
    // 14:00 UK time (BST): outside quiet hours.
    private static readonly DateTimeOffset Afternoon = new(2026, 10, 19, 13, 0, 0, TimeSpan.Zero);

    private static readonly Guid Gary = Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67");
    private static readonly Guid Sam = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly FixedClock clock = new(Afternoon);
    private readonly FakeBatches batches = new();
    private readonly FakeStockPushScheduler scheduler = new();
    private readonly FakePushSubscriptions subscriptions = new();
    private readonly FakePushSeen seen = new();
    private readonly FakePushSender sender = new();
    private readonly ListLogger<AddStockChangeToPush> addLogs = new();
    private readonly ListLogger<SendStockChangePush> sendLogs = new();

    private StockPushSettings settings = StockPushSettings.Default;

    private AddStockChangeToPush Add() => new(batches, scheduler, settings, TestSettings.Stock(), clock, addLogs);

    private SendStockChangePush Sender() => new(batches, subscriptions, seen,
        new SendPush(subscriptions, seen, sender, clock, new ListLogger<SendPush>()), settings, TestSettings.Stock(), clock, sendLogs);

    private static StockChange Change(string name, StockChangeKind kind = StockChangeKind.Added, Guid? scanId = null) =>
        new(StockChangeKeys.ForScan(scanId ?? Guid.NewGuid()), kind, name, Afternoon);

    private Task AddAt(StockChange change, TimeSpan after)
    {
        clock.Now = Afternoon + after;
        return Add().ExecuteAsync(change with { At = clock.Now }, CancellationToken.None);
    }

    private void Device(Guid user, string endpoint, bool stockChanges = true) =>
        subscriptions.Items[(user, endpoint)] = new PushSubscription(endpoint, user, $"https://web.push.apple.com/{endpoint}", "p", "a", "iPhone",
            Afternoon, Afternoon, null, new Dictionary<string, bool> { ["stockChanges"] = stockChanges });

    // ---- Batching

    [Fact]
    public async Task The_first_change_opens_a_batch_due_a_minute_later()
    {
        await AddAt(Change("Milk"), TimeSpan.Zero);

        var batch = batches.Open!.Value;
        Assert.Equal(["Milk"], batch.Changes.Select(c => c.Name));
        Assert.Equal((batch.BatchId, TimeSpan.FromSeconds(60)), Assert.Single(scheduler.Scheduled));
    }

    [Fact]
    public async Task Each_change_pushes_the_send_back_to_a_minute_after_it_but_never_past_five_minutes_after_the_first()
    {
        await AddAt(Change("Milk"), TimeSpan.Zero);
        await AddAt(Change("Bread"), TimeSpan.FromSeconds(30)); // due 1:30
        await AddAt(Change("Eggs"), TimeSpan.FromSeconds(270)); // 5:30 after the first → capped at 5:00

        Assert.Equal([TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)], scheduler.Scheduled.Select(s => s.Delay));
        Assert.Single(scheduler.Scheduled.Select(s => s.BatchId).Distinct()); // all one batch
        Assert.Equal(3, batches.Open!.Value.Changes.Count);
    }

    [Fact]
    public async Task The_delays_come_from_settings()
    {
        settings = new StockPushSettings(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));

        await AddAt(Change("Milk"), TimeSpan.Zero);
        await AddAt(Change("Bread"), TimeSpan.FromSeconds(8));

        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(7)], scheduler.Scheduled.Select(s => s.Delay));
    }

    [Fact]
    public async Task A_replayed_change_is_only_in_the_batch_once()
    {
        var milk = Change("Milk");
        await AddAt(milk, TimeSpan.Zero);
        await AddAt(milk, TimeSpan.FromSeconds(5));

        Assert.Single(batches.Open!.Value.Changes);
        Assert.Single(scheduler.Scheduled);
    }

    [Fact]
    public async Task A_concurrent_change_is_retried_not_lost()
    {
        await AddAt(Change("Milk"), TimeSpan.Zero);
        var open = batches.Open!.Value;
        batches.BeforeWrite = () => batches.Set(open with { Changes = [.. open.Changes, Change("Bread")] });

        await AddAt(Change("Eggs"), TimeSpan.FromSeconds(10));

        Assert.Equal(["Milk", "Bread", "Eggs"], batches.Open!.Value.Changes.Select(c => c.Name));
    }

    [Theory]
    [InlineData(2026, 10, 19, 20, 30)] // 21:30 BST
    [InlineData(2026, 10, 20, 6, 29)] // 07:29 BST
    [InlineData(2026, 12, 1, 21, 45)] // 21:45 GMT
    [InlineData(2026, 12, 2, 7, 15)] // 07:15 GMT
    public async Task Changes_in_quiet_hours_are_never_batched(int y, int mo, int d, int h, int mi)
    {
        clock.Now = new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero);

        await Add().ExecuteAsync(Change("Milk"), CancellationToken.None);

        Assert.Null(batches.Open);
        Assert.Empty(scheduler.Scheduled);
    }

    [Fact]
    public async Task A_failure_is_a_warning_and_never_throws()
    {
        batches.Fail = true;

        await AddAt(Change("Milk"), TimeSpan.Zero);

        Assert.Contains(addLogs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    // ---- Sending

    private async Task<Guid> BatchOf(params StockChange[] changes)
    {
        foreach (var change in changes)
        {
            await AddAt(change, TimeSpan.Zero);
        }

        clock.Now = Afternoon + TimeSpan.FromSeconds(60);
        return batches.Open!.Value.BatchId;
    }

    [Fact]
    public async Task Everyone_opted_in_gets_one_push_for_the_batch()
    {
        Device(Gary, "gary-phone");
        Device(Gary, "gary-laptop");
        Device(Sam, "sam-phone");
        Device(Sam, "sam-old", stockChanges: false);
        var batchId = await BatchOf(Change("Milk"), Change("Bread"), Change("Hummus", StockChangeKind.Removed));

        var summary = await Sender().ExecuteAsync(batchId, CancellationToken.None);

        Assert.Equal(new StockPushSummary(3, 2, 2, 0), summary);
        Assert.Equal(["gary-laptop", "gary-phone", "sam-phone"], sender.Sent.Select(s => s.Subscription.Id).Order());
        var message = sender.Sent[0].Message;
        // Something was removed, so tapping it opens the "add to the list?" sheet for this batch (#89).
        Assert.Equal(("Cupboard updated", "Added Milk and Bread · Removed Hummus", $"/?removed={batchId:D}", "stockChanges"), (message.Title, message.Body, message.Path, message.Topic));
        Assert.Null(batches.Open); // closed
        Assert.Equal(3, batches.Sent[batchId].Changes.Count); // kept for the sheet
    }

    [Fact]
    public void Every_topic_the_app_sends_is_one_Apple_accepts()
    {
        // Apple refused every "stock" push with 400 BadWebPushTopic (#106): it reads the topic as Base64.
        Assert.Null(PushMessage.Problem(SendStockChangePush.Message([Change("Milk")], Guid.NewGuid())));
        Assert.Null(PushMessage.Problem(SendTestPush.Message));
    }

    [Fact]
    public async Task A_push_with_nothing_removed_just_opens_the_stock_list()
    {
        Device(Gary, "gary-phone");
        var batchId = await BatchOf(Change("Milk"));

        await Sender().ExecuteAsync(batchId, CancellationToken.None);

        Assert.Equal("/", sender.Sent[0].Message.Path);
    }

    [Fact]
    public async Task People_who_saw_every_change_on_any_device_get_nothing_and_others_get_what_they_missed()
    {
        Device(Gary, "gary-phone");
        Device(Sam, "sam-phone");
        var milk = Change("Milk");
        var bread = Change("Bread");
        var batchId = await BatchOf(milk, bread);
        seen.Receipts.Add((Gary, milk.EventKey)); // e.g. on his laptop
        seen.Receipts.Add((Gary, bread.EventKey));
        seen.Receipts.Add((Sam, milk.EventKey));

        var summary = await Sender().ExecuteAsync(batchId, CancellationToken.None);

        Assert.Equal(new StockPushSummary(2, 2, 1, 1), summary);
        var (subscription, message) = Assert.Single(sender.Sent);
        Assert.Equal(("sam-phone", "Added Bread"), (subscription.Id, message.Body));
    }

    [Fact]
    public async Task Nobody_opted_in_means_no_push()
    {
        Device(Gary, "gary-phone", stockChanges: false);
        var batchId = await BatchOf(Change("Milk"));

        Assert.Equal(new StockPushSummary(1, 0, 0, 0), await Sender().ExecuteAsync(batchId, CancellationToken.None));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_redelivered_or_stale_message_sends_nothing()
    {
        Device(Gary, "gary-phone");
        var batchId = await BatchOf(Change("Milk"));

        await Sender().ExecuteAsync(batchId, CancellationToken.None);
        Assert.Null(await Sender().ExecuteAsync(batchId, CancellationToken.None)); // re-delivered

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_message_for_a_batch_pushed_back_by_a_later_change_waits_for_the_later_one()
    {
        Device(Gary, "gary-phone");
        await AddAt(Change("Milk"), TimeSpan.Zero);
        await AddAt(Change("Bread"), TimeSpan.FromSeconds(40)); // now due at 1:40
        var batchId = batches.Open!.Value.BatchId;

        clock.Now = Afternoon + TimeSpan.FromSeconds(60); // the first message
        Assert.Null(await Sender().ExecuteAsync(batchId, CancellationToken.None));
        Assert.Empty(sender.Sent);

        clock.Now = Afternoon + TimeSpan.FromSeconds(100); // the second
        Assert.NotNull(await Sender().ExecuteAsync(batchId, CancellationToken.None));
        Assert.Equal("Added Milk and Bread", Assert.Single(sender.Sent).Message.Body);
    }

    [Fact]
    public async Task A_batch_falling_due_in_quiet_hours_is_closed_without_sending()
    {
        Device(Gary, "gary-phone");
        clock.Now = new DateTimeOffset(2026, 10, 19, 20, 29, 30, TimeSpan.Zero); // 21:29:30 BST
        await Add().ExecuteAsync(Change("Milk"), CancellationToken.None);
        var batchId = batches.Open!.Value.BatchId;

        clock.Now += TimeSpan.FromSeconds(60); // 21:30:30
        var summary = await Sender().ExecuteAsync(batchId, CancellationToken.None);

        Assert.Equal(new StockPushSummary(1, 0, 0, 0), summary);
        Assert.Empty(sender.Sent);
        Assert.Null(batches.Open); // never sent later
    }

    [Fact]
    public async Task A_push_failure_for_one_device_still_pushes_the_others()
    {
        Device(Gary, "gary-phone");
        Device(Sam, "sam-phone");
        sender.Outcome = s => s.UserId == Gary ? PushOutcome.Failed : PushOutcome.Delivered;
        var batchId = await BatchOf(Change("Milk"));

        await Sender().ExecuteAsync(batchId, CancellationToken.None);

        Assert.Equal(2, sender.Sent.Count);
    }

    // ---- The body

    private static readonly string[] SevenThings = ["Milk", "Bread", "Eggs", "Rice", "Pasta", "Beans", "Oats"];

    [Fact]
    public void The_body_lists_up_to_three_names_a_side_with_repeats_counted_and_empty_sides_left_out()
    {
        static StockChange Removed(string name) => Change(name, StockChangeKind.Removed);

        Assert.Equal("Added Milk", SendStockChangePush.Body([Change("Milk")]));
        Assert.Equal("Removed Hummus", SendStockChangePush.Body([Removed("Hummus")]));
        Assert.Equal("Added 2 × Milk and Bread", SendStockChangePush.Body([Change("Milk"), Change("Bread"), Change("milk")]));
        Assert.Equal("Added Milk, Bread and Eggs", SendStockChangePush.Body([Change("Milk"), Change("Bread"), Change("Eggs")]));
        Assert.Equal(
            "Added Milk, Bread, Eggs and 4 more · Removed Hummus",
            SendStockChangePush.Body([.. SevenThings.Select(n => Change(n)), Removed("Hummus")]));
    }

    // ---- From the scanners

    private const string Bisto = "5000354922831";
    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FakeLookup lookup = new();

    private ProcessScanEvent Processor()
    {
        var feed = new FakeFeed();
        return new ProcessScanEvent(
            new CheckInScan(new ProductResolver(products, lookup, clock, new ListLogger<ProductResolver>()), units, TestSettings.Stock(),
                feed.Notifier(), clock, new ListLogger<CheckInScan>(), Add()),
            new CheckOutScan(products, units, new FakeUnmatched(), feed.Notifier(), clock, new ListLogger<CheckOutScan>(), Add()),
            new StoreLabelCheckInScan(new FakeStoreLabels(), feed.Notifier(), clock, new ListLogger<StoreLabelCheckInScan>()),
            new ListLogger<ProcessScanEvent>());
    }

    [Fact]
    public async Task Scanner_check_ins_and_check_outs_of_named_products_join_the_batch_once()
    {
        lookup.Results.Enqueue(new ProductLookupResult.Found(new ProductDetails("Beef Gravy", "Bisto", "230g", null, [])));
        var checkIn = TestScans.Make(Bisto, Role.In, at: Afternoon);
        var checkOut = TestScans.Make(Bisto, Role.Out, at: Afternoon);

        await Processor().ExecuteAsync(checkIn, CancellationToken.None);
        await Processor().ExecuteAsync(checkIn, CancellationToken.None); // replay
        await Processor().ExecuteAsync(checkOut, CancellationToken.None);
        await Processor().ExecuteAsync(TestScans.Make(Bisto, Role.Out, at: Afternoon), CancellationToken.None); // nothing in stock: amber

        Assert.Equal(
            [(StockChangeKeys.ForScan(checkIn.ScanId), StockChangeKind.Added, "Beef Gravy"), (StockChangeKeys.ForScan(checkOut.ScanId), StockChangeKind.Removed, "Beef Gravy")],
            batches.Open!.Value.Changes.Select(c => (c.EventKey, c.Kind, c.Name)));
    }

    [Fact]
    public async Task Unnamed_check_ins_are_not_pushed_and_a_push_failure_never_fails_the_scan()
    {
        lookup.Results.Enqueue(new ProductLookupResult.NotFound());
        await Processor().ExecuteAsync(TestScans.Make(Bisto, Role.In, at: Afternoon), CancellationToken.None);
        Assert.Null(batches.Open);

        batches.Fail = true;
        lookup.Results.Enqueue(new ProductLookupResult.Found(new ProductDetails("Beef Gravy", "Bisto", "230g", null, [])));
        await Processor().ExecuteAsync(TestScans.Make("5000168001142", Role.In, at: Afternoon), CancellationToken.None);
        Assert.Equal(2, units.Units.Count);
    }

    // ---- App changes (#81)

    private static readonly CurrentUser GaryUser = new(Gary, "Gary");
    private static readonly CurrentUser SamUser = new(Sam, "Sam");

    private Guid KnownProduct(string name, int inStock = 0)
    {
        var id = Guid.NewGuid();
        products.Products[id] = Product.Found(id, "5000354922831", new ProductDetails(name, null, null, null, []), Afternoon, 1);
        for (var i = 0; i < inStock; i++)
        {
            units.Add(Unit.AddedInApp(Guid.NewGuid(), id, Sam, Afternoon.AddDays(-1), null));
        }

        return id;
    }

    private CheckOutFromApp AppCheckOut() => new(products, units, clock, new ListLogger<CheckOutFromApp>(), Add());

    private UndoCheckOut Undo() => new(units, new ListLogger<UndoCheckOut>(), products, Add(), clock);

    private AddToStock Adder() =>
        new(products, units, new ProductMatcher(products), TestSettings.Stock(), clock, new ListLogger<AddToStock>(), Add());

    private async Task<Guid> Sent()
    {
        var batchId = batches.Open!.Value.BatchId;
        clock.Now = batches.Open.Value.DueAt(settings);
        await Sender().ExecuteAsync(batchId, CancellationToken.None);
        return batchId;
    }

    [Fact]
    public async Task An_app_check_out_pushes_everyone_but_the_person_who_did_it_on_all_their_devices()
    {
        Device(Gary, "gary-phone");
        Device(Gary, "gary-laptop");
        Device(Sam, "sam-phone");
        var hummus = KnownProduct("Hummus", inStock: 2);

        var result = await AppCheckOut().ExecuteAsync(hummus, GaryUser, CancellationToken.None);
        await Sent();

        Assert.IsType<CheckOutFromAppResult.CheckedOut>(result);
        var (subscription, message) = Assert.Single(sender.Sent);
        Assert.Equal(("sam-phone", "Removed Hummus"), (subscription.Id, message.Body));
        var change = Assert.Single(batches.Closed[0].Changes);
        Assert.Equal((Gary, hummus), (change.ActorId, change.ProductId));
        Assert.StartsWith("app:", change.EventKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gone_from_still_got_this_pushes_nobody_and_neither_does_its_undo()
    {
        Device(Sam, "sam-phone");
        var hummus = KnownProduct("Hummus", inStock: 1);
        var unitId = units.InStock(hummus).Single().UnitId;

        var gone = await new CheckOutUnit(products, units, clock, new ListLogger<CheckOutUnit>(), Add())
            .ExecuteAsync(unitId, GaryUser, CancellationToken.None, CheckOutReasons.StockCheck);
        await Undo().ExecuteAsync(unitId, CancellationToken.None, GaryUser);

        Assert.IsType<CheckOutUnitResult.CheckedOut>(gone);
        Assert.Null(batches.Open); // nothing joined a batch, so nothing will be pushed
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_check_out_undone_in_the_same_batch_nets_out_and_nobody_is_pushed()
    {
        Device(Sam, "sam-phone");
        var hummus = KnownProduct("Hummus", inStock: 1);

        var checkedOut = (CheckOutFromAppResult.CheckedOut)await AppCheckOut().ExecuteAsync(hummus, GaryUser, CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(4);
        await Undo().ExecuteAsync(checkedOut.UnitId, CancellationToken.None, GaryUser);
        await Sent();

        Assert.Equal(2, batches.Closed[0].Changes.Count);
        Assert.Empty(sender.Sent);
        Assert.Contains(sendLogs.Entries, e => e.Message.Contains("0 pushed, 1 saw everything", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adding_from_the_add_tab_pushes_each_unit_once_even_when_the_request_is_retried()
    {
        Device(Gary, "gary-phone");
        var bananas = KnownProduct("Bananas");
        var requestId = Guid.NewGuid();
        AddItem[] items = [new(bananas, null, null, null, 2, AddSource.Pick)];

        await Adder().ExecuteAsync(requestId, items, SamUser, CancellationToken.None);
        await Adder().ExecuteAsync(requestId, items, SamUser, CancellationToken.None); // a retry
        await Sent();

        Assert.Equal(2, batches.Closed[0].Changes.Count);
        Assert.Equal("Added 2 × Bananas", Assert.Single(sender.Sent).Message.Body);
    }

    [Fact]
    public async Task Assigning_a_store_label_pushes_it_as_added()
    {
        Device(Gary, "gary-phone");
        var storeLabels = new FakeStoreLabels();
        var feed = new FakeFeed();
        var scan = TestScans.Make("2012345678903", Role.In, at: Afternoon);
        await new StoreLabelCheckInScan(storeLabels, feed.Notifier(), clock, new ListLogger<StoreLabelCheckInScan>())
            .ExecuteAsync(scan, CancellationToken.None);
        var assign = new AssignStoreLabel(storeLabels, units, Adder(), TestSettings.Stock(), feed.Notifier(), clock,
            new ListLogger<AssignStoreLabel>(), Add());

        await assign.ExecuteAsync(scan.ScanId, new AddItem(null, "Ham hock terrine", null, null, 1, AddSource.Typed), SamUser, CancellationToken.None);
        await assign.ExecuteAsync(scan.ScanId, new AddItem(null, "Ham hock terrine", null, null, 1, AddSource.Typed), SamUser, CancellationToken.None);
        await Sent();

        Assert.Equal("Added Ham hock terrine", Assert.Single(sender.Sent).Message.Body);
    }

    [Fact]
    public async Task A_mixed_batch_gives_each_person_what_they_missed()
    {
        Device(Gary, "gary-phone");
        Device(Sam, "sam-phone");
        var hummus = KnownProduct("Hummus", inStock: 1);
        var milk = Change("Milk") with { ProductId = Guid.NewGuid() };
        await AddAt(milk, TimeSpan.Zero); // a scanner check-in
        seen.Receipts.Add((Sam, milk.EventKey)); // Sam saw its toast
        await AppCheckOut().ExecuteAsync(hummus, SamUser, CancellationToken.None); // and checked out the hummus herself

        await Sent();

        var (subscription, message) = Assert.Single(sender.Sent);
        Assert.Equal(("gary-phone", "Added Milk · Removed Hummus"), (subscription.Id, message.Body));
    }

    [Fact]
    public void Netting_works_per_product()
    {
        var milk = Guid.NewGuid();
        StockChange C(StockChangeKind kind) => Change("Milk", kind) with { ProductId = milk };

        Assert.Equal("Added Milk", SendStockChangePush.Body([C(StockChangeKind.Added), C(StockChangeKind.Added), C(StockChangeKind.Removed)]));
        Assert.Equal("Removed 2 × Milk", SendStockChangePush.Body([C(StockChangeKind.Removed), C(StockChangeKind.Removed)]));
        Assert.Equal("", SendStockChangePush.Body([C(StockChangeKind.Removed), C(StockChangeKind.Added)]));
        Assert.Empty(SendStockChangePush.Net([C(StockChangeKind.Removed), C(StockChangeKind.Added)]));
    }

    [Fact]
    public async Task An_undo_without_a_signed_in_person_or_a_push_failure_never_affects_the_action()
    {
        var hummus = KnownProduct("Hummus", inStock: 1);
        var checkedOut = (CheckOutFromAppResult.CheckedOut)await AppCheckOut().ExecuteAsync(hummus, GaryUser, CancellationToken.None);
        batches.Fail = true;

        var undone = await Undo().ExecuteAsync(checkedOut.UnitId, CancellationToken.None, GaryUser);
        var again = await AppCheckOut().ExecuteAsync(hummus, GaryUser, CancellationToken.None);

        Assert.True(Assert.IsType<UndoCheckOutResult.InStock>(undone).WasRestored);
        Assert.IsType<CheckOutFromAppResult.CheckedOut>(again);
        Assert.Contains(addLogs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }
}
