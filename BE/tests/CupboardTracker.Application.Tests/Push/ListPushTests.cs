using CupboardTracker.Application.Push;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Application.Tests.ShoppingList;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Push;

internal sealed class FakeListBatches : IListPushBatchRepository
{
    private int version;

    public Versioned<ListPushBatch>? Open { get; private set; }

    public List<ListPushBatch> Closed { get; } = [];

    public bool Fail { get; set; }

    public Task<Versioned<ListPushBatch>?> GetOpenAsync(CancellationToken cancellationToken) =>
        Fail ? throw new InvalidOperationException("Cosmos is down") : Task.FromResult(Open);

    public Task<bool> TryCreateAsync(ListPushBatch batch, CancellationToken cancellationToken)
    {
        if (Open is not null)
        {
            return Task.FromResult(false);
        }

        Open = new(batch, $"v{++version}");
        return Task.FromResult(true);
    }

    public Task<bool> TryReplaceAsync(Versioned<ListPushBatch> current, ListPushBatch updated, CancellationToken cancellationToken)
    {
        if (Open?.ETag != current.ETag)
        {
            return Task.FromResult(false);
        }

        Open = new(updated, $"v{++version}");
        return Task.FromResult(true);
    }

    public Task<bool> TryCloseAsync(Versioned<ListPushBatch> current, CancellationToken cancellationToken)
    {
        if (Open?.ETag != current.ETag)
        {
            return Task.FromResult(false);
        }

        Closed.Add(Open.Value);
        Open = null;
        return Task.FromResult(true);
    }
}

internal sealed class FakeListViewers : IListViewerRepository
{
    private int version;

    public Dictionary<Guid, Versioned<ListViewer>> Items { get; } = [];

    public ListViewer? this[Guid person] => Items.GetValueOrDefault(person)?.Value;

    public void Set(ListViewer viewer) => Items[viewer.PersonId] = new(viewer, $"v{++version}");

    public Task<Versioned<ListViewer>?> GetAsync(Guid personId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.GetValueOrDefault(personId));

    public Task<bool> TryCreateAsync(ListViewer viewer, CancellationToken cancellationToken)
    {
        if (Items.ContainsKey(viewer.PersonId))
        {
            return Task.FromResult(false);
        }

        Set(viewer);
        return Task.FromResult(true);
    }

    public Task<bool> TryReplaceAsync(Versioned<ListViewer> current, ListViewer updated, CancellationToken cancellationToken)
    {
        if (Items.GetValueOrDefault(updated.PersonId)?.ETag != current.ETag)
        {
            return Task.FromResult(false);
        }

        Set(updated);
        return Task.FromResult(true);
    }
}

internal sealed class FakeListPushScheduler : IListPushScheduler
{
    public List<(Guid BatchId, TimeSpan Delay)> Batches { get; } = [];

    public List<(Guid PersonId, DateTimeOffset DueAt, TimeSpan Delay)> FollowUps { get; } = [];

    public Task ScheduleBatchAsync(Guid batchId, TimeSpan delay, CancellationToken cancellationToken)
    {
        Batches.Add((batchId, delay));
        return Task.CompletedTask;
    }

    public Task ScheduleFollowUpAsync(Guid personId, DateTimeOffset dueAt, TimeSpan delay, CancellationToken cancellationToken)
    {
        FollowUps.Add((personId, dueAt, delay));
        return Task.CompletedTask;
    }
}

// The shopping list push (#86).
public sealed class ListPushTests
{
    // 14:00 UK time (BST): outside quiet hours.
    private static readonly DateTimeOffset Afternoon = new(2026, 10, 21, 13, 0, 0, TimeSpan.Zero);

    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");
    private static readonly CurrentUser Sam = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Sam");

    private readonly FixedClock clock = new(Afternoon);
    private readonly FakeListBatches batches = new();
    private readonly FakeListViewers viewers = new();
    private readonly FakeListPushScheduler scheduler = new();
    private readonly FakeShoppingList list = new();
    private readonly FakePushSubscriptions subscriptions = new();
    private readonly FakePushSender sender = new();
    private readonly ListLogger<AddListItemToPush> addLogs = new();
    private readonly ListLogger<SendListPush> sendLogs = new();

    private ListPushSettings settings = ListPushSettings.Default;

    private AddListItemToPush Add() => new(batches, scheduler, settings, TestSettings.Stock(), clock, addLogs);

    private SendListPush Sender() => new(batches, viewers, scheduler, list, subscriptions,
        new SendPush(subscriptions, new FakePushSeen(), sender, clock, new ListLogger<SendPush>()), settings, TestSettings.Stock(), clock, sendLogs);

    private ShoppingListItem Item(string name, CurrentUser by, TimeSpan after, string source = ListItemSources.Typed,
        ListItemStatus status = ListItemStatus.Open, bool removed = false)
    {
        var at = Afternoon + after;
        var item = new ShoppingListItem(Guid.NewGuid(), name, ShoppingListNames.Normalize(name), null, null, "other", "jev", source,
            status, by.ObjectId, by.DisplayName, at, null, null, null, at, 1, "c", removed);
        list.Items[item.Id] = new(item, "e");
        return item;
    }

    private async Task AddAt(ShoppingListItem item, TimeSpan after)
    {
        clock.Now = Afternoon + after;
        await Add().ExecuteAsync(item, CancellationToken.None);
    }

    private void Device(CurrentUser user, string endpoint, bool? shoppingList = true)
    {
        var preferences = new Dictionary<string, bool> { ["stockChanges"] = true };
        if (shoppingList is { } on)
        {
            preferences["shoppingList"] = on;
        }

        subscriptions.Items[(user.ObjectId, endpoint)] = new PushSubscription(endpoint, user.ObjectId, $"https://web.push.apple.com/{endpoint}",
            "p", "a", "iPhone", Afternoon, Afternoon, null, preferences);
    }

    /// <summary>Runs the batch that's open now, at its due time.</summary>
    private async Task<ListPushSummary?> SendDue()
    {
        var open = batches.Open!.Value;
        clock.Now = open.DueAt(settings);
        return await Sender().ExecuteBatchAsync(open.BatchId, CancellationToken.None);
    }

    private IEnumerable<Guid> PushedTo() => sender.Sent.Select(s => s.Subscription.UserId);

    // ---- Batching

    [Fact]
    public async Task The_first_add_opens_a_batch_due_three_minutes_later()
    {
        await AddAt(Item("Milk", Sam, TimeSpan.Zero), TimeSpan.Zero);

        var batch = batches.Open!.Value;
        Assert.Equal((Afternoon, Afternoon, 1), (batch.FirstAt, batch.LastAt, batch.Adds));
        Assert.Equal(TimeSpan.FromMinutes(3), Assert.Single(scheduler.Batches).Delay);
    }

    [Fact]
    public async Task Later_adds_push_it_back_but_never_past_fifteen_minutes_after_the_first()
    {
        for (var minute = 0; minute <= 14; minute += 2)
        {
            await AddAt(Item($"Thing {minute}", Sam, TimeSpan.FromMinutes(minute)), TimeSpan.FromMinutes(minute));
        }

        var batch = batches.Open!.Value;
        Assert.Equal(8, batch.Adds);
        Assert.Equal(Afternoon + TimeSpan.FromMinutes(15), batch.DueAt(settings)); // 14 + 3 would be 17
        Assert.Equal(TimeSpan.FromMinutes(1), scheduler.Batches[^1].Delay);
        Assert.Equal(TimeSpan.FromMinutes(3), scheduler.Batches[1].Delay); // at minute 2: due at 5
    }

    [Fact]
    public async Task The_settings_are_respected()
    {
        settings = new ListPushSettings(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2));

        await AddAt(Item("Milk", Sam, TimeSpan.Zero), TimeSpan.Zero);
        await AddAt(Item("Bread", Sam, TimeSpan.FromSeconds(50)), TimeSpan.FromSeconds(50));

        Assert.Equal([TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10)], scheduler.Batches.Select(b => b.Delay));
    }

    [Fact]
    public async Task Automatic_adds_never_open_or_extend_a_batch()
    {
        await AddAt(Item("Milk", Sam, TimeSpan.Zero, ListItemSources.AutoRunOut), TimeSpan.Zero);
        Assert.Null(batches.Open);

        await AddAt(Item("Bread", Sam, TimeSpan.Zero), TimeSpan.Zero);
        await AddAt(Item("Eggs", Sam, TimeSpan.FromMinutes(2), ListItemSources.AutoRunOut), TimeSpan.FromMinutes(2));
        Assert.Equal((1, Afternoon), (batches.Open!.Value.Adds, batches.Open.Value.LastAt));
    }

    [Theory]
    [InlineData(2026, 10, 21, 20, 30)] // 21:30 BST
    [InlineData(2026, 10, 22, 6, 29)] // 07:29 BST
    [InlineData(2026, 12, 1, 21, 45)] // 21:45 GMT
    [InlineData(2026, 12, 2, 7, 15)] // 07:15 GMT
    public async Task Adds_in_quiet_hours_never_open_a_batch(int y, int mo, int d, int h, int mi)
    {
        clock.Now = new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero);

        await Add().ExecuteAsync(Item("Milk", Sam, TimeSpan.Zero), CancellationToken.None);

        Assert.Null(batches.Open);
        Assert.Empty(scheduler.Batches);
    }

    [Fact]
    public async Task A_failure_is_a_warning_and_never_fails_the_add()
    {
        batches.Fail = true;

        await AddAt(Item("Milk", Sam, TimeSpan.Zero), TimeSpan.Zero);

        Assert.Contains(addLogs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("won't trigger a push", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adding_through_the_list_opens_the_batch_but_a_merge_into_an_open_item_does_not()
    {
        var adder = new AddToShoppingList(list, new FakeProducts(), new FakeAisleScheduler(),
            new ShoppingListNotifier(new FakeListPublisher(), new ListLogger<ShoppingListNotifier>()), clock,
            new ListLogger<AddToShoppingList>(), Add());

        await adder.ExecuteAsync(new AddListItem(Guid.NewGuid(), "c1", "Milk", null, null, ListItemSources.Typed), Sam, CancellationToken.None);
        clock.Now = Afternoon.AddMinutes(1);
        await adder.ExecuteAsync(new AddListItem(Guid.NewGuid(), "c2", "milk", null, null, ListItemSources.Typed), Gary, CancellationToken.None);
        await adder.ExecuteAsync(new AddListItem(Guid.NewGuid(), "c3", "Bin bags", null, null, ListItemSources.AutoRunOut), Gary, CancellationToken.None);

        Assert.Equal((1, Afternoon), (batches.Open!.Value.Adds, batches.Open.Value.LastAt));
    }

    // ---- Sending

    [Fact]
    public async Task Everyone_else_gets_one_push_with_what_they_havent_seen_oldest_first()
    {
        Device(Gary, "gary-phone");
        Device(Sam, "sam-phone");
        await AddAt(Item("Milk", Sam, TimeSpan.Zero), TimeSpan.Zero);
        await AddAt(Item("Bin bags", Sam, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(1));
        await AddAt(Item("Bread", Sam, TimeSpan.FromMinutes(2)), TimeSpan.FromMinutes(2));

        var summary = await SendDue();

        var (subscription, message) = Assert.Single(sender.Sent);
        Assert.Equal(Gary.ObjectId, subscription.UserId);
        Assert.Equal(("Shopping list", "3 new things on the list: Milk, Bin bags and Bread", "/list", "list", TimeSpan.FromHours(6)),
            (message.Title, message.Body, message.Path, message.Topic, message.TimeToLive));
        Assert.Equal(new ListPushSummary(3, 2, 1, 1, 0), summary); // Sam only added his own
        Assert.Equal(clock.Now, viewers[Gary.ObjectId]!.LastPushAt);
        Assert.Single(batches.Closed);
    }

    [Fact]
    public async Task Only_items_added_since_they_last_looked_and_still_open_are_mentioned()
    {
        Device(Gary, "gary-phone");
        Item("Milk", Sam, TimeSpan.FromMinutes(-30)); // before Gary last looked
        viewers.Set(new ListViewer(Gary.ObjectId, Afternoon.AddMinutes(-10), null, null));
        Item("Bread", Sam, TimeSpan.FromMinutes(-5), status: ListItemStatus.Done); // ticked since
        Item("Eggs", Sam, TimeSpan.FromMinutes(-4), removed: true); // removed since
        Item("Butter", Gary, TimeSpan.FromMinutes(-3)); // his own
        await AddAt(Item("Tea", Sam, TimeSpan.Zero), TimeSpan.Zero);

        await SendDue();

        Assert.Equal("1 new thing on the list: Tea", Assert.Single(sender.Sent).Message.Body);
    }

    [Fact]
    public async Task Someone_who_has_looked_since_gets_nothing()
    {
        Device(Gary, "gary-phone");
        await AddAt(Item("Milk", Sam, TimeSpan.Zero), TimeSpan.Zero);
        clock.Now = Afternoon.AddMinutes(1);
        await new RecordListViewed(viewers, clock, new ListLogger<RecordListViewed>()).ExecuteAsync(Gary.ObjectId, CancellationToken.None);

        var summary = await SendDue();

        Assert.Empty(sender.Sent);
        Assert.Equal(1, summary!.NothingNew);
        Assert.Null(viewers[Gary.ObjectId]!.LastPushAt);
    }

    [Fact]
    public async Task Within_the_gap_it_waits_for_one_follow_up_which_sends_everything_new_then()
    {
        Device(Gary, "gary-phone");
        viewers.Set(new ListViewer(Gary.ObjectId, null, Afternoon.AddMinutes(-10), null)); // pushed 10 minutes ago
        await AddAt(Item("Milk", Sam, TimeSpan.Zero), TimeSpan.Zero);

        var first = await SendDue();

        Assert.Empty(sender.Sent);
        Assert.Equal(1, first!.HeldBack);
        var dueAt = Afternoon.AddMinutes(20);
        var followUp = Assert.Single(scheduler.FollowUps);
        Assert.Equal((Gary.ObjectId, dueAt, dueAt - clock.Now), followUp);
        Assert.Equal(dueAt, viewers[Gary.ObjectId]!.FollowUpAt);

        // Another batch within the gap: the follow-up already waits.
        await AddAt(Item("Bread", Sam, TimeSpan.FromMinutes(8)), TimeSpan.FromMinutes(8));
        await SendDue();
        Assert.Single(scheduler.FollowUps);
        Assert.Empty(sender.Sent);

        clock.Now = dueAt;
        Assert.Equal(ListPushDecision.Sent, await Sender().ExecuteFollowUpAsync(Gary.ObjectId, dueAt, CancellationToken.None));
        Assert.Equal("2 new things on the list: Milk and Bread", Assert.Single(sender.Sent).Message.Body);
        Assert.Equal((dueAt, (DateTimeOffset?)null), (viewers[Gary.ObjectId]!.LastPushAt!.Value, viewers[Gary.ObjectId]!.FollowUpAt));

        // Delivered again: nothing.
        Assert.Null(await Sender().ExecuteFollowUpAsync(Gary.ObjectId, dueAt, CancellationToken.None));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_follow_up_with_nothing_new_left_sends_nothing()
    {
        Device(Gary, "gary-phone");
        var dueAt = Afternoon.AddMinutes(20);
        viewers.Set(new ListViewer(Gary.ObjectId, Afternoon.AddMinutes(15), Afternoon.AddMinutes(-10), dueAt)); // looked since
        Item("Milk", Sam, TimeSpan.Zero);
        clock.Now = dueAt;

        Assert.Equal(ListPushDecision.NothingNew, await Sender().ExecuteFollowUpAsync(Gary.ObjectId, dueAt, CancellationToken.None));
        Assert.Empty(sender.Sent);
    }

    [Theory]
    [InlineData(1, "1 new thing on the list: Milk")]
    [InlineData(2, "2 new things on the list: Milk and Bin bags")]
    [InlineData(3, "3 new things on the list: Milk, Bin bags and Bread")]
    [InlineData(5, "5 new things on the list: Milk, Bin bags, Bread and 2 more")]
    public void The_body_names_up_to_three(int count, string body) =>
        Assert.Equal(body, SendListPush.Body([.. Names.Take(count)]));

    private static readonly string[] Names = ["Milk", "Bin bags", "Bread", "Eggs", "Tea"];

    [Fact]
    public void The_topic_is_one_Apple_accepts() => Assert.True(Domain.Push.PushTopic.IsValid(SendListPush.Topic));

    [Fact]
    public async Task A_batch_sends_once_and_not_before_it_is_due()
    {
        Device(Gary, "gary-phone");
        await AddAt(Item("Milk", Sam, TimeSpan.Zero), TimeSpan.Zero);
        var batchId = batches.Open!.Value.BatchId;

        clock.Now = Afternoon.AddMinutes(1);
        Assert.Null(await Sender().ExecuteBatchAsync(batchId, CancellationToken.None));
        Assert.NotNull(batches.Open);

        await SendDue();
        Assert.Null(await Sender().ExecuteBatchAsync(batchId, CancellationToken.None));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Nothing_is_sent_when_it_falls_due_in_quiet_hours()
    {
        Device(Gary, "gary-phone");
        clock.Now = new DateTimeOffset(2026, 10, 21, 20, 28, 0, TimeSpan.Zero); // 21:28 BST
        await Add().ExecuteAsync(Item("Milk", Sam, TimeSpan.Zero), CancellationToken.None);

        await SendDue(); // 21:31

        Assert.Empty(sender.Sent);
        Assert.Null(batches.Open);
    }

    [Fact]
    public async Task A_follow_up_in_quiet_hours_sends_nothing()
    {
        Device(Gary, "gary-phone");
        var dueAt = new DateTimeOffset(2026, 10, 21, 20, 45, 0, TimeSpan.Zero); // 21:45 BST
        viewers.Set(new ListViewer(Gary.ObjectId, null, dueAt.AddMinutes(-30), dueAt));
        Item("Milk", Sam, TimeSpan.Zero);
        clock.Now = dueAt;

        Assert.Null(await Sender().ExecuteFollowUpAsync(Gary.ObjectId, dueAt, CancellationToken.None));
        Assert.Empty(sender.Sent);
        Assert.Null(viewers[Gary.ObjectId]!.FollowUpAt);
    }

    [Fact]
    public async Task Devices_turned_off_are_skipped_and_ones_never_asked_count_as_on()
    {
        Device(Gary, "gary-phone", shoppingList: false);
        Device(Sam, "sam-phone", shoppingList: null); // turned on before #86
        var alex = new CurrentUser(Guid.NewGuid(), "Alex");
        await AddAt(Item("Milk", alex, TimeSpan.Zero), TimeSpan.Zero);

        await SendDue();

        Assert.Equal([Sam.ObjectId], PushedTo());
    }

    [Fact]
    public async Task Viewed_records_server_time_and_is_idempotent()
    {
        var record = new RecordListViewed(viewers, clock, new ListLogger<RecordListViewed>());

        await record.ExecuteAsync(Gary.ObjectId, CancellationToken.None);
        await record.ExecuteAsync(Gary.ObjectId, CancellationToken.None);

        Assert.Equal(new ListViewer(Gary.ObjectId, Afternoon, null, null), viewers[Gary.ObjectId]);
    }
}
