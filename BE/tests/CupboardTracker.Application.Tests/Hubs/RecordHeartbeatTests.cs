using CupboardTracker.Application.Hubs;
using CupboardTracker.Domain;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Hubs;

public sealed class RecordHeartbeatTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 18, 0, 0, TimeSpan.Zero);

    private readonly FakeHubs hubs = new();
    private readonly FixedClock clock = new(Now);
    private readonly ListLogger<RecordHeartbeat> logger = new();

    private RecordHeartbeat UseCase() => new(hubs, clock, logger);

    private static RecordHeartbeatCommand Valid(
        string? hubId = "cupboard-pi",
        string? sentAt = "2026-10-17T17:55:00.000Z", // the Pi's clock: 5 minutes slow
        IReadOnlyList<ScannerPresenceCommand>? scanners = null) =>
        new(hubId, sentAt, "abc1234", 3600, ["USB Adapter USB Device"], scanners);

    private static Hub StoredHub(DateTimeOffset lastSeen, DateTimeOffset? alertSentAt) =>
        new("cupboard-pi", lastSeen, alertSentAt, new HeartbeatDetails(lastSeen, "old", 1, [], null));

    [Fact]
    public async Task First_heartbeat_creates_the_hub_with_server_time()
    {
        var result = await UseCase().ExecuteAsync(Valid(), CancellationToken.None);

        Assert.IsType<RecordHeartbeatResult.Recorded>(result);
        var hub = hubs.Stored["cupboard-pi"].Hub;
        Assert.Equal(Now, hub.LastSeenAt); // not the client's sentAt
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 17, 55, 0, TimeSpan.Zero), hub.LastHeartbeat.SentAt);
        Assert.Null(hub.OfflineAlertSentAt);
        Assert.Equal(["USB Adapter USB Device"], hub.LastHeartbeat.UsbInputDevices);
        Assert.Null(hub.LastHeartbeat.Scanners); // absent: not stored
    }

    [Fact]
    public async Task Scanner_presence_is_stored_when_sent()
    {
        await UseCase().ExecuteAsync(
            Valid(scanners: [new("scanner-in", true), new("scanner-out", false)]), CancellationToken.None);

        Assert.Equal(
            [new ScannerPresence("scanner-in", true), new ScannerPresence("scanner-out", false)],
            hubs.Stored["cupboard-pi"].Hub.LastHeartbeat.Scanners);
    }

    [Fact]
    public async Task A_heartbeat_ends_an_outage_clears_the_flag_and_logs_back_online()
    {
        hubs.Put(StoredHub(Now.AddMinutes(-42), alertSentAt: Now.AddMinutes(-20)));

        await UseCase().ExecuteAsync(Valid(), CancellationToken.None);

        var hub = hubs.Stored["cupboard-pi"].Hub;
        Assert.Null(hub.OfflineAlertSentAt);
        Assert.Equal(Now, hub.LastSeenAt);
        Assert.Contains("Hub cupboard-pi back online after 42 minutes", Assert.Single(logger.At(LogLevel.Information)));
    }

    [Fact]
    public async Task An_ordinary_heartbeat_logs_no_back_online()
    {
        hubs.Put(StoredHub(Now.AddMinutes(-2), alertSentAt: null));

        await UseCase().ExecuteAsync(Valid(), CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("back online", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retries_when_the_offline_check_changes_the_hub_at_the_same_moment()
    {
        hubs.Put(StoredHub(Now.AddMinutes(-30), alertSentAt: null));
        hubs.BeforeWrite = () => hubs.Put(hubs.Stored["cupboard-pi"].Hub with { OfflineAlertSentAt = Now });

        await UseCase().ExecuteAsync(Valid(), CancellationToken.None);

        var hub = hubs.Stored["cupboard-pi"].Hub;
        Assert.Null(hub.OfflineAlertSentAt);
        Assert.Equal(Now, hub.LastSeenAt);
    }

    [Fact]
    public async Task Invalid_heartbeats_list_every_problem_and_store_nothing()
    {
        var result = await UseCase().ExecuteAsync(
            new RecordHeartbeatCommand("Cupboard Pi", "2026-10-17 17:55", "", -1, null, [new("scanner-in", null)]),
            CancellationToken.None);

        var invalid = Assert.IsType<RecordHeartbeatResult.Invalid>(result);
        Assert.Equal(6, invalid.Errors.Count);
        Assert.Empty(hubs.Stored);
    }

    [Theory]
    [InlineData(33, 10)]
    [InlineData(1, 201)]
    public async Task Usb_device_list_is_bounded(int count, int length)
    {
        var names = Enumerable.Repeat(new string('x', length), count).ToList();
        var result = await UseCase().ExecuteAsync(
            Valid() with { UsbInputDevices = names }, CancellationToken.None);

        Assert.IsType<RecordHeartbeatResult.Invalid>(result);
    }

    [Fact]
    public async Task An_empty_usb_device_list_is_fine()
    {
        var result = await UseCase().ExecuteAsync(Valid() with { UsbInputDevices = [] }, CancellationToken.None);

        Assert.IsType<RecordHeartbeatResult.Recorded>(result);
    }
}
