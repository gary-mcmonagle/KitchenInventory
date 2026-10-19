using CupboardTracker.Application.Push;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Push;

internal sealed class FakePushSubscriptions : IPushSubscriptionRepository
{
    public Dictionary<(Guid, string), PushSubscription> Items { get; } = [];

    public List<(Guid, string, DateTimeOffset)> Delivered { get; } = [];

    public Task<PushSubscription?> GetAsync(Guid userId, string id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.GetValueOrDefault((userId, id)));

    public Task UpsertAsync(PushSubscription subscription, CancellationToken cancellationToken)
    {
        Items[(subscription.UserId, subscription.Id)] = subscription;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid userId, string id, CancellationToken cancellationToken) => Task.FromResult(Items.Remove((userId, id)));

    public Task<IReadOnlyList<PushSubscription>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PushSubscription>>([.. Items.Values]);

    public Task MarkDeliveredAsync(Guid userId, string id, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Delivered.Add((userId, id, at));
        return Task.CompletedTask;
    }
}

internal sealed class FakePushSeen : IPushSeenRepository
{
    public List<(Guid User, string Key)> Receipts { get; } = [];

    public HashSet<Guid> Claimed { get; } = [];

    public Task RecordAsync(Guid userId, IReadOnlyCollection<string> eventKeys, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Receipts.AddRange(eventKeys.Select(k => (userId, k)));
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<Guid>> UsersWhoSawAsync(string eventKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(Receipts.Where(r => r.Key == eventKey).Select(r => r.User).ToHashSet());

    public Task<bool> TryClaimJobAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Claimed.Add(jobId));
}

internal sealed class FakePushSender : IPushSender
{
    public List<(PushSubscription Subscription, PushMessage Message)> Sent { get; } = [];

    public Func<PushSubscription, PushOutcome> Outcome { get; set; } = _ => PushOutcome.Delivered;

    public Task<PushOutcome> SendAsync(PushSubscription subscription, PushMessage message, CancellationToken cancellationToken)
    {
        Sent.Add((subscription, message));
        return Task.FromResult(Outcome(subscription));
    }
}

internal sealed class FakeScheduler : IPushJobScheduler
{
    public List<(PushJob Job, TimeSpan Delay)> Jobs { get; } = [];

    public Task ScheduleAsync(PushJob job, TimeSpan delay, CancellationToken cancellationToken)
    {
        Jobs.Add((job, delay));
        return Task.CompletedTask;
    }
}

public sealed class PushTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");
    private static readonly CurrentUser Sam = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Sam");

    // A real P-256 point (RFC 8291's user agent key) and a 16-byte auth secret.
    private const string P256dh = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string Auth = "BTBZMqHH6r4Tts7J_aSIgg";

    private static readonly PushMessage Message = new("Cupboard updated", "Added Milk", "/", "stock", TimeSpan.FromHours(1));

    private readonly FakePushSubscriptions subscriptions = new();
    private readonly FakePushSeen seen = new();
    private readonly FakePushSender sender = new();
    private readonly FixedClock clock = new(Now);
    private readonly ListLogger<SendPush> sendLogs = new();

    private SendPush Send() => new(subscriptions, seen, sender, clock, sendLogs);

    private PushSubscription Device(CurrentUser user, string endpoint, params (string Type, bool On)[] prefs)
    {
        var s = new PushSubscription(PushSubscriptionRules.IdFor(endpoint), user.ObjectId, endpoint, P256dh, Auth, "iPhone", Now, Now, null,
            prefs.ToDictionary(p => p.Type, p => p.On));
        subscriptions.Items[(user.ObjectId, s.Id)] = s;
        return s;
    }

    [Fact]
    public async Task Targets_everyone_opted_in_to_a_type()
    {
        var garyPhone = Device(Gary, "https://web.push.apple.com/a", ("stockChanges", true));
        Device(Gary, "https://fcm.googleapis.com/fcm/send/b", ("stockChanges", false));
        Device(Sam, "https://fcm.googleapis.com/fcm/send/c");

        var summary = await Send().ExecuteAsync(Message, new PushAudience { OptedInTo = "stockChanges" }, CancellationToken.None);

        Assert.Equal(garyPhone, Assert.Single(sender.Sent).Subscription);
        Assert.Equal(new PushSendSummary(1, 1, 0, 0), summary);
        Assert.Equal((Gary.ObjectId, garyPhone.Id, Now), Assert.Single(subscriptions.Delivered));
    }

    [Fact]
    public async Task Targets_named_users_or_everyone_except_some()
    {
        Device(Gary, "https://web.push.apple.com/a");
        Device(Sam, "https://fcm.googleapis.com/fcm/send/c");

        await Send().ExecuteAsync(Message, new PushAudience { OnlyUsers = [Sam.ObjectId] }, CancellationToken.None);
        Assert.Equal([Sam.ObjectId], sender.Sent.Select(s => s.Subscription.UserId));

        sender.Sent.Clear();
        await Send().ExecuteAsync(Message, new PushAudience { ExceptUsers = [Sam.ObjectId] }, CancellationToken.None);
        Assert.Equal([Gary.ObjectId], sender.Sent.Select(s => s.Subscription.UserId));
    }

    [Fact]
    public async Task People_who_saw_the_event_on_any_device_are_left_out()
    {
        Device(Gary, "https://web.push.apple.com/phone");
        Device(Gary, "https://fcm.googleapis.com/fcm/send/laptop");
        Device(Sam, "https://fcm.googleapis.com/fcm/send/sam");
        await new RecordPushSeen(seen, clock).ExecuteAsync(["scan:1"], Gary, CancellationToken.None); // seen on one device

        await Send().ExecuteAsync(Message, new PushAudience { ExceptSeenEventKey = "scan:1" }, CancellationToken.None);

        Assert.Equal([Sam.ObjectId], sender.Sent.Select(s => s.Subscription.UserId)); // neither of Gary's devices
    }

    [Fact]
    public async Task Gone_subscriptions_are_deleted_and_failures_never_throw()
    {
        var gone = Device(Gary, "https://web.push.apple.com/gone");
        Device(Gary, "https://fcm.googleapis.com/fcm/send/refused");
        Device(Sam, "https://fcm.googleapis.com/fcm/send/boom");
        sender.Outcome = s => s.Endpoint.EndsWith("gone", StringComparison.Ordinal) ? PushOutcome.Gone
            : s.Endpoint.EndsWith("refused", StringComparison.Ordinal) ? PushOutcome.Refused
            : throw new HttpRequestException("boom");

        var summary = await Send().ExecuteAsync(Message, new PushAudience(), CancellationToken.None);

        Assert.Equal(new PushSendSummary(3, 0, 1, 2), summary);
        Assert.DoesNotContain((Gary.ObjectId, gone.Id), subscriptions.Items.Keys);
        Assert.Contains(sendLogs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("refused", StringComparison.Ordinal));
        Assert.Contains(sendLogs.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Without_keys_nothing_is_sent_beyond_the_first_try_and_a_bad_message_is_never_sent()
    {
        Device(Gary, "https://web.push.apple.com/a");
        Device(Sam, "https://fcm.googleapis.com/fcm/send/b");
        sender.Outcome = _ => PushOutcome.NotConfigured;
        await Send().ExecuteAsync(Message, new PushAudience(), CancellationToken.None);
        Assert.Single(sender.Sent);

        sender.Sent.Clear();
        await Send().ExecuteAsync(Message with { Topic = "use-first" }, new PushAudience(), CancellationToken.None);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_scheduled_job_runs_once_even_if_the_message_is_delivered_twice()
    {
        Device(Gary, "https://web.push.apple.com/a");
        var scheduler = new FakeScheduler();
        await new SchedulePush(scheduler).ExecuteAsync(Message, new PushAudience(), TimeSpan.FromMinutes(1), CancellationToken.None);
        var (job, delay) = Assert.Single(scheduler.Jobs);
        Assert.Equal(TimeSpan.FromMinutes(1), delay);

        var run = new RunPushJob(seen, Send(), clock, new ListLogger<RunPushJob>());
        Assert.NotNull(await run.ExecuteAsync(job, CancellationToken.None));
        Assert.Null(await run.ExecuteAsync(job, CancellationToken.None));

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Receipts_are_stored_for_the_caller_and_bad_keys_are_refused()
    {
        var record = new RecordPushSeen(seen, clock);

        Assert.Null(await record.ExecuteAsync(["scan:1", "scan:2", "scan:1"], Gary, CancellationToken.None));
        Assert.Equal([(Gary.ObjectId, "scan:1"), (Gary.ObjectId, "scan:2")], seen.Receipts);

        Assert.NotNull(await record.ExecuteAsync([], Gary, CancellationToken.None));
        Assert.NotNull(await record.ExecuteAsync(["a/b"], Gary, CancellationToken.None));
        Assert.NotNull(await record.ExecuteAsync([new string('x', 101)], Gary, CancellationToken.None));
        Assert.NotNull(await record.ExecuteAsync([.. Enumerable.Range(0, 51).Select(i => $"k{i}")], Gary, CancellationToken.None));
    }

    [Fact]
    public async Task Registering_validates_the_endpoint_and_keys_and_keeps_preferences_on_refresh()
    {
        var register = new RegisterPushSubscription(subscriptions, clock, new ListLogger<RegisterPushSubscription>());
        const string endpoint = "https://web.push.apple.com/abc";

        var saved = Assert.IsType<PushSubscriptionResult.Saved>(await register.ExecuteAsync(
            new PushSubscriptionInput(endpoint, P256dh, Auth), "Mozilla/5.0 (iPhone; CPU iPhone OS 18_4 like Mac OS X)", Gary, CancellationToken.None));
        Assert.Equal(("iPhone", Gary.ObjectId, Now), (saved.Subscription.DeviceLabel, saved.Subscription.UserId, saved.Subscription.LastSeenAt));

        await new SetPushPreferences(subscriptions).ExecuteAsync(endpoint, new Dictionary<string, bool> { ["stockChanges"] = true }, Gary, CancellationToken.None);
        clock.Now = Now.AddDays(3);
        var refreshed = Assert.IsType<PushSubscriptionResult.Saved>(await register.ExecuteAsync(new PushSubscriptionInput(endpoint, P256dh, Auth), null, Gary, CancellationToken.None));
        Assert.Equal((Now, Now.AddDays(3)), (refreshed.Subscription.CreatedAt, refreshed.Subscription.LastSeenAt));
        Assert.True(refreshed.Subscription.Wants("stockChanges"));

        foreach (var bad in new[]
        {
            new PushSubscriptionInput("https://evil.example/push", P256dh, Auth),
            new PushSubscriptionInput(endpoint, "AAAA", Auth),
            new PushSubscriptionInput(endpoint, P256dh, "AAAA"),
            new PushSubscriptionInput(null, P256dh, Auth),
        })
        {
            Assert.IsType<PushSubscriptionResult.Invalid>(await register.ExecuteAsync(bad, null, Gary, CancellationToken.None));
        }
    }

    [Fact]
    public async Task People_can_only_remove_or_change_their_own_subscriptions()
    {
        const string endpoint = "https://web.push.apple.com/garys";
        Device(Gary, endpoint);

        Assert.IsType<PushSubscriptionResult.NotFound>(await new RemovePushSubscription(subscriptions).ExecuteAsync(endpoint, Sam, CancellationToken.None));
        Assert.IsType<PushSubscriptionResult.NotFound>(await new SetPushPreferences(subscriptions)
            .ExecuteAsync(endpoint, new Dictionary<string, bool> { ["stockChanges"] = true }, Sam, CancellationToken.None));
        Assert.IsType<PushSubscriptionResult.Invalid>(await new SetPushPreferences(subscriptions)
            .ExecuteAsync(endpoint, new Dictionary<string, bool> { ["stock-changes"] = true }, Gary, CancellationToken.None));

        Assert.IsType<PushSubscriptionResult.Removed>(await new RemovePushSubscription(subscriptions).ExecuteAsync(endpoint, Gary, CancellationToken.None));
        Assert.Empty(subscriptions.Items);
    }

    [Fact]
    public async Task Subscriptions_not_seen_for_30_days_are_deleted()
    {
        Device(Gary, "https://web.push.apple.com/fresh");
        var stale = Device(Sam, "https://fcm.googleapis.com/fcm/send/stale");
        subscriptions.Items[(Sam.ObjectId, stale.Id)] = stale with { LastSeenAt = Now.AddDays(-31) };

        var removed = await new ExpireStalePushSubscriptions(subscriptions, clock, new ListLogger<ExpireStalePushSubscriptions>()).ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Equal([Gary.ObjectId], subscriptions.Items.Values.Select(s => s.UserId));
    }

    [Fact]
    public async Task The_test_push_goes_to_the_callers_own_devices_only_a_few_times_an_hour()
    {
        Device(Gary, "https://web.push.apple.com/a");
        Device(Sam, "https://fcm.googleapis.com/fcm/send/b");
        var test = new SendTestPush(subscriptions, Send(), new PushSettings("public-key"), new PushTestSettings(PerHour: 2), clock);

        Assert.IsType<SendTestPushResult.Sent>(await test.ExecuteAsync(Gary, CancellationToken.None));
        Assert.IsType<SendTestPushResult.Sent>(await test.ExecuteAsync(Gary, CancellationToken.None));
        Assert.IsType<SendTestPushResult.TooMany>(await test.ExecuteAsync(Gary, CancellationToken.None));
        Assert.All(sender.Sent, s => Assert.Equal(Gary.ObjectId, s.Subscription.UserId));
        Assert.Equal("Test notification", sender.Sent[0].Message.Title);

        clock.Now = Now.AddHours(1);
        Assert.IsType<SendTestPushResult.Sent>(await test.ExecuteAsync(Gary, CancellationToken.None));
        Assert.IsType<SendTestPushResult.NoSubscriptions>(await test.ExecuteAsync(new CurrentUser(Guid.NewGuid(), "Nobody"), CancellationToken.None));
        Assert.IsType<SendTestPushResult.NotConfigured>(await new SendTestPush(subscriptions, Send(), new PushSettings(null), new PushTestSettings(), clock)
            .ExecuteAsync(Gary, CancellationToken.None));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) Chrome/130", "Android")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Chrome/130", "Mac")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Edg/130", "Windows")]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 18_4 like Mac OS X)", "iPad")]
    [InlineData(null, "Browser")]
    public void Device_labels_come_from_the_user_agent(string? userAgent, string label) =>
        Assert.Equal(label, PushSubscriptionRules.DeviceLabel(userAgent));
}
