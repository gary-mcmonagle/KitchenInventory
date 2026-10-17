using CupboardTracker.Application.Hubs;
using CupboardTracker.Application.Notifications;
using CupboardTracker.Domain;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Hubs;

public sealed class CheckHubsOfflineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(20);

    private readonly FakeHubs hubs = new();
    private readonly FakeNotifier notifier = new();
    private readonly FixedClock clock = new(Now);
    private readonly ListLogger<CheckHubsOffline> logger = new();

    private CheckHubsOffline UseCase() => new(
        hubs, notifier, new OfflineAlertSettings(Threshold, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")),
        clock, logger);

    private void HubLastSeen(TimeSpan ago, DateTimeOffset? alertSentAt = null) =>
        hubs.Put(new Hub("cupboard-pi", Now - ago, alertSentAt, new HeartbeatDetails(Now - ago, "abc1234", 60, [], null)));

    [Fact]
    public async Task Exactly_at_the_threshold_is_not_offline()
    {
        HubLastSeen(Threshold);

        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task Just_over_the_threshold_sends_one_alert_and_marks_the_hub()
    {
        HubLastSeen(Threshold + TimeSpan.FromSeconds(1));

        await UseCase().ExecuteAsync(CancellationToken.None);

        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationKind.OfflineAlert, sent.Kind);
        Assert.Equal("Cupboard Tracker: Pi offline", sent.Content.Subject);
        Assert.Contains("cupboard-pi", sent.Content.PlainText, StringComparison.Ordinal);
        Assert.Equal(Now, hubs.Stored["cupboard-pi"].Hub.OfflineAlertSentAt);
    }

    [Fact]
    public async Task The_next_run_sends_nothing_while_it_stays_offline()
    {
        HubLastSeen(TimeSpan.FromMinutes(25));
        await UseCase().ExecuteAsync(CancellationToken.None);
        clock.Now = Now.AddMinutes(5);

        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task A_hub_that_never_sent_a_heartbeat_never_alerts()
    {
        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task After_it_comes_back_a_later_outage_alerts_again()
    {
        HubLastSeen(TimeSpan.FromMinutes(25));
        await UseCase().ExecuteAsync(CancellationToken.None);
        var heartbeat = new RecordHeartbeat(hubs, clock, new ListLogger<RecordHeartbeat>());
        await heartbeat.ExecuteAsync(
            new RecordHeartbeatCommand("cupboard-pi", "2026-10-17T18:00:00Z", "abc1234", 1, [], null), CancellationToken.None);
        Assert.Null(hubs.Stored["cupboard-pi"].Hub.OfflineAlertSentAt);

        clock.Now = Now.AddMinutes(21);
        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(2, notifier.Sent.Count);
    }

    [Fact]
    public async Task A_heartbeat_arriving_during_the_check_wins_and_no_email_goes()
    {
        HubLastSeen(TimeSpan.FromMinutes(25));
        hubs.BeforeWrite = () => HubLastSeen(TimeSpan.Zero);

        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Empty(notifier.Sent);
        Assert.Null(hubs.Stored["cupboard-pi"].Hub.OfflineAlertSentAt);
    }

    [Fact]
    public async Task A_failed_send_logs_an_error_and_the_next_run_tries_again()
    {
        HubLastSeen(TimeSpan.FromMinutes(25));
        notifier.FailWith = new InvalidOperationException("ACS said no");

        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("cupboard-pi", StringComparison.Ordinal));
        Assert.Null(hubs.Stored["cupboard-pi"].Hub.OfflineAlertSentAt);

        notifier.FailWith = null;
        clock.Now = Now.AddMinutes(5);
        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Single(notifier.Sent);
    }
}
