using System.Text.Json;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Infrastructure.SignalR;
using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure.Tests;

/// <summary>The scan feed's wire format is spike #38's contract (add fields, never rename).</summary>
public sealed class ScanFeedWireTests
{
    private static readonly Guid Scan = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Product = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Unit = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset At = new(2026, 10, 18, 7, 30, 0, TimeSpan.Zero);

    private static string Json(ScanFeedEvent e) => JsonSerializer.Serialize(ScanFeedWire.Payload(e));

    [Fact]
    public void Scan_identified()
    {
        var e = new ScanIdentified(Scan, "scanner-out", Role.Out, At, ScanOutcome.CheckedOut, Product, "Beef Gravy", "Bisto", null, Unit);
        Assert.Equal("scanIdentified", e.Name);
        Assert.Equal(
            """{"scanId":"11111111-1111-1111-1111-111111111111","deviceId":"scanner-out","role":"out","occurredAt":"2026-10-18T07:30:00.000Z","outcome":"checkedOut","productId":"22222222-2222-2222-2222-222222222222","displayName":"Beef Gravy","brand":"Bisto","size":null,"unitId":"33333333-3333-3333-3333-333333333333"}""",
            Json(e));
    }

    [Fact]
    public void Scan_needs_identification()
    {
        var e = new ScanNeedsIdentification(Scan, "scanner-in", At, Product, "20055363", "Unknown item (20055363)", ProductStatus.LookupFailed, Unit);
        Assert.Equal("scanNeedsIdentification", e.Name);
        Assert.Equal(
            """{"scanId":"11111111-1111-1111-1111-111111111111","deviceId":"scanner-in","role":"in","occurredAt":"2026-10-18T07:30:00.000Z","productId":"22222222-2222-2222-2222-222222222222","barcode":"20055363","displayName":"Unknown item (20055363)","productStatus":"lookupFailed","unitId":"33333333-3333-3333-3333-333333333333"}""",
            Json(e));
    }

    [Fact]
    public void Check_out_unmatched_skipped_named_and_dismissed()
    {
        Assert.Equal(
            """{"scanId":"11111111-1111-1111-1111-111111111111","deviceId":"scanner-out","role":"out","occurredAt":"2026-10-18T07:30:00.000Z","barcode":"20055363","productId":null,"displayName":null}""",
            Json(new CheckOutUnmatched(Scan, "scanner-out", At, "20055363", null, null)));
        Assert.Equal(
            """{"scanId":"11111111-1111-1111-1111-111111111111","deviceId":"scanner-in","role":"in","occurredAt":"2026-10-18T07:30:00.000Z","barcode":"2012345678903","reason":"storeLabel"}""",
            Json(new ScanSkipped(Scan, "scanner-in", Role.In, At, "2012345678903", ScanSkipped.StoreLabel)));
        Assert.Equal("""{"occurredAt":"2026-10-18T07:30:00.000Z","productId":"22222222-2222-2222-2222-222222222222","displayName":"Mustard"}""",
            Json(new ProductNamed(Product, "Mustard", At)));
        Assert.Equal("""{"occurredAt":"2026-10-18T07:30:00.000Z","productId":"22222222-2222-2222-2222-222222222222"}""",
            Json(new FeedDismissed(Product, At)));
        Assert.Equal(["checkOutUnmatched", "scanSkipped", "productNamed", "feedDismissed", "scanReceived"],
            new ScanFeedEvent[]
            {
                new CheckOutUnmatched(Scan, "d", At, "1", null, null), new ScanSkipped(Scan, "d", Role.In, At, "1", "x"),
                new ProductNamed(Product, "x", At), new FeedDismissed(Product, At), new ScanReceived(Scan, "d", Role.In, At, "1", At),
            }.Select(e => e.Name));
    }

    [Fact]
    public void Options_read_the_identity_settings_or_the_emulator_and_never_show_a_connection_string()
    {
        var azure = SignalROptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureSignalRConnectionString:serviceUri"] = "https://sigr.service.signalr.net",
            ["AzureSignalRConnectionString:credential"] = "managedidentity",
        }).Build());
        Assert.Equal(("https://sigr.service.signalr.net/", null, true), (azure.ServiceUri!.ToString(), azure.ConnectionString, azure.IsConfigured));

        var emulator = SignalROptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureSignalRConnectionString"] = "Endpoint=http://localhost;Port=8888;AccessKey=emulator-key;Version=1.0;",
        }).Build());
        Assert.True(emulator.IsConfigured);
        Assert.DoesNotContain("emulator-key", emulator.ToString(), StringComparison.Ordinal);

        Assert.False(SignalROptions.FromConfiguration(new ConfigurationBuilder().Build()).IsConfigured);
    }

    [Fact]
    public async Task Without_signalr_publishing_does_nothing()
    {
        var feed = new SignalRScanFeed(new SignalROptions(null, null));
        await feed.PublishAsync(new FeedDismissed(Product, At), CancellationToken.None); // no throw, no network
    }
}
