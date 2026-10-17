using System.Text.Json;
using CupboardTracker.Application.Scans;
using CupboardTracker.Domain;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Scans;

public sealed class RecordScanTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 14, 0, 0, TimeSpan.Zero);
    private const string ScanId = "0b1c3f1e-6d0e-4c0a-9e0a-3a4d7c1f9a21";

    private readonly FakeScanEvents scanEvents = new();
    private readonly FakeDevices devices = new();
    private readonly ListLogger logger = new();

    private RecordScan UseCase() => new(scanEvents, devices, new FixedClock(Now), logger);

    private static RecordScanCommand Valid(
        string? scanId = ScanId, string? barcode = "5000354922831", string? deviceId = "scanner-in",
        string? role = "in", string? scannedAt = "2026-10-17T13:59:58.123Z", string? clientSha = "abc1234",
        Dictionary<string, JsonElement>? extra = null) =>
        new(scanId, barcode, deviceId, role, scannedAt, clientSha, extra ?? []);

    [Fact]
    public async Task Stores_a_valid_scan_with_server_receive_time()
    {
        var result = await UseCase().ExecuteAsync(Valid(), CancellationToken.None);

        var recorded = Assert.IsType<RecordScanResult.Recorded>(result);
        Assert.False(recorded.WasAlreadyRecorded);
        var stored = Assert.Single(scanEvents.Stored).Event;
        Assert.Equal(Guid.Parse(ScanId), stored.ScanId);
        Assert.Equal("5000354922831", stored.Barcode.Value);
        Assert.Equal(Role.In, stored.Role);
        Assert.Equal(Now, stored.ReceivedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 13, 59, 58, 123, TimeSpan.Zero), stored.ScannedAt);
    }

    [Fact]
    public async Task A_resend_is_success_and_stores_nothing_new()
    {
        await UseCase().ExecuteAsync(Valid(), CancellationToken.None);
        var second = await UseCase().ExecuteAsync(Valid(), CancellationToken.None);

        var recorded = Assert.IsType<RecordScanResult.Recorded>(second);
        Assert.True(recorded.WasAlreadyRecorded);
        Assert.Single(scanEvents.Stored);
    }

    [Fact]
    public async Task Extra_fields_are_passed_through_as_is()
    {
        var extra = new Dictionary<string, JsonElement> { ["source"] = JsonSerializer.SerializeToElement("simulator") };
        await UseCase().ExecuteAsync(Valid(extra: extra), CancellationToken.None);
        Assert.Equal("simulator", Assert.Single(scanEvents.Stored).Extra["source"].GetString());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("receivedAt")]
    [InlineData("_etag")]
    public async Task Reserved_extra_fields_are_rejected(string name)
    {
        var extra = new Dictionary<string, JsonElement> { [name] = JsonSerializer.SerializeToElement("x") };
        var result = await UseCase().ExecuteAsync(Valid(extra: extra), CancellationToken.None);
        Assert.Contains(name, Assert.IsType<RecordScanResult.Invalid>(result).Errors.Single(), StringComparison.Ordinal);
        Assert.Empty(scanEvents.Stored);
    }

    public static TheoryData<RecordScanCommand, string> InvalidCommands => new()
    {
        { Valid(barcode: "5000354922832"), "check digit" },
        { Valid(barcode: "12345"), "8, 12 or 13" },
        { Valid(barcode: null), "barcode is required" },
        { Valid(role: "both"), "role" },
        { Valid(role: "IN"), "role" },
        { Valid(scanId: "not-a-uuid"), "scanId" },
        { Valid(scanId: null), "scanId" },
        { Valid(deviceId: "Scanner In"), "deviceId" },
        { Valid(scannedAt: "yesterday"), "scannedAt" },
        { Valid(scannedAt: "2026-10-17T13:59:58"), "scannedAt" }, // no timezone
        { Valid(clientSha: ""), "clientSha" },
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public async Task Invalid_scans_are_rejected_with_a_reason_and_not_stored(RecordScanCommand command, string reason)
    {
        var result = await UseCase().ExecuteAsync(command, CancellationToken.None);

        var invalid = Assert.IsType<RecordScanResult.Invalid>(result);
        Assert.Contains(invalid.Errors, e => e.Contains(reason, StringComparison.Ordinal));
        Assert.Empty(scanEvents.Stored);
        Assert.Empty(devices.Stored);
    }

    [Fact]
    public async Task Offsets_are_normalised_to_utc()
    {
        await UseCase().ExecuteAsync(Valid(scannedAt: "2026-10-17T14:59:58.000+01:00"), CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, scanEvents.Stored.Single().Event.ScannedAt.Offset);
        Assert.Equal(13, scanEvents.Stored.Single().Event.ScannedAt.Hour);
    }

    [Fact]
    public async Task Creates_the_device_with_its_role_and_times()
    {
        await UseCase().ExecuteAsync(Valid(), CancellationToken.None);

        var device = devices.Stored["scanner-in"];
        Assert.Equal(Role.In, device.Role);
        Assert.Equal(Now, device.LastSeenAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 13, 59, 58, 123, TimeSpan.Zero), device.LastScanAt);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task A_role_change_updates_the_device_and_logs_a_warning()
    {
        devices.Stored["scanner-in"] = new Device("scanner-in", Role.Out, Now.AddDays(-1), Now.AddDays(-1));

        await UseCase().ExecuteAsync(Valid(), CancellationToken.None);

        Assert.Equal(Role.In, devices.Stored["scanner-in"].Role);
        Assert.Contains("scanner-in", Assert.Single(logger.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_older_queued_scan_never_moves_last_scan_backwards()
    {
        var latest = new DateTimeOffset(2026, 10, 17, 13, 59, 59, TimeSpan.Zero);
        devices.Stored["scanner-in"] = new Device("scanner-in", Role.In, Now, latest);

        await UseCase().ExecuteAsync(Valid(scannedAt: "2026-10-17T12:00:00Z"), CancellationToken.None);

        Assert.Equal(latest, devices.Stored["scanner-in"].LastScanAt);
    }

    private sealed class FakeScanEvents : IScanEventRepository
    {
        public List<(ScanEvent Event, IReadOnlyDictionary<string, JsonElement> Extra)> Stored { get; } = [];

        public Task<bool> CreateIfNotExistsAsync(
            ScanEvent scanEvent, IReadOnlyDictionary<string, JsonElement> extraFields, CancellationToken cancellationToken)
        {
            if (Stored.Any(s => s.Event.ScanId == scanEvent.ScanId))
            {
                return Task.FromResult(false);
            }

            Stored.Add((scanEvent, extraFields));
            return Task.FromResult(true);
        }
    }

    private sealed class FakeDevices : IDeviceRepository
    {
        public Dictionary<string, Device> Stored { get; } = [];

        public Task<Device?> GetAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.GetValueOrDefault(deviceId));

        public Task UpsertAsync(Device device, CancellationToken cancellationToken)
        {
            Stored[device.DeviceId] = device;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ListLogger : ILogger<RecordScan>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
