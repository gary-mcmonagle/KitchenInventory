using CupboardTracker.Application.Adding;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Push;
using CupboardTracker.Application.Stock;
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
        Assert.Equal(("Cupboard updated", "Added Milk and Bread · Removed Hummus", "/", "stock"), (message.Title, message.Body, message.Path, message.Topic));
        Assert.Null(batches.Open); // closed
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
}
