using CupboardTracker.Application.Notifications;
using CupboardTracker.Domain;
using CupboardTracker.Infrastructure.Cosmos;
using CupboardTracker.Infrastructure.Email;
using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure.Tests;

public sealed class HubAndEmailTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static readonly IConfiguration EmailConfig = Config(
        ("ACS_ENDPOINT", "https://acs-cupboard-test.uk.communication.azure.com"),
        ("EMAIL_SENDER", "DoNotReply@example.azurecomm.net"),
        ("ALERT_EMAIL_TO", "alerts@example.com"));

    [Fact]
    public void Offline_alerts_go_to_the_alert_address()
    {
        var options = EmailOptions.FromConfiguration(EmailConfig);
        var message = AcsEmailNotifier.ToMessage(
            new Notification(NotificationKind.OfflineAlert, new EmailContent("Subject", "<p>Html</p>", "Plain")), options);

        Assert.Equal("DoNotReply@example.azurecomm.net", message.SenderAddress);
        Assert.Equal("alerts@example.com", Assert.Single(message.Recipients.To).Address);
        Assert.Equal("Subject", message.Content.Subject);
        Assert.Equal("<p>Html</p>", message.Content.Html);
        Assert.Equal("Plain", message.Content.PlainText);
    }

    [Fact]
    public void A_missing_recipient_names_the_setting()
    {
        var options = EmailOptions.FromConfiguration(Config(
            ("ACS_ENDPOINT", "https://acs.example.com"), ("EMAIL_SENDER", "DoNotReply@example.azurecomm.net")));

        var error = Assert.Throws<InvalidOperationException>(() => options.RecipientFor(NotificationKind.OfflineAlert));
        Assert.Contains("ALERT_EMAIL_TO", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_acs_settings_are_named() =>
        Assert.Contains("ACS_ENDPOINT",
            Assert.Throws<InvalidOperationException>(() => EmailOptions.FromConfiguration(Config())).Message,
            StringComparison.Ordinal);

    [Fact]
    public void Offline_threshold_defaults_to_20_minutes_and_can_be_set()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), ServiceCollectionExtensions.OfflineAlertSettingsFromConfiguration(Config()).Threshold);
        var settings = ServiceCollectionExtensions.OfflineAlertSettingsFromConfiguration(Config(("OFFLINE_THRESHOLD_MINUTES", "4")));
        Assert.Equal(TimeSpan.FromMinutes(4), settings.Threshold);
        Assert.Equal("Europe/London", settings.HomeTimeZone.Id);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("twenty")]
    public void A_bad_threshold_fails_loudly(string value) =>
        Assert.Throws<InvalidOperationException>(() =>
            ServiceCollectionExtensions.OfflineAlertSettingsFromConfiguration(Config(("OFFLINE_THRESHOLD_MINUTES", value))));

    [Fact]
    public void Hub_documents_round_trip_with_scanner_presence()
    {
        var hub = new Hub(
            "cupboard-pi",
            new DateTimeOffset(2026, 10, 17, 18, 0, 0, 123, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 17, 18, 25, 0, TimeSpan.Zero),
            new HeartbeatDetails(
                new DateTimeOffset(2026, 10, 17, 17, 59, 58, TimeSpan.Zero), "abc1234", 3600, ["USB Adapter USB Device"],
                [new ScannerPresence("scanner-in", true), new ScannerPresence("scanner-out", false)]));

        var document = CosmosHubRepository.Write(hub);

        Assert.Equal("hub", document["kind"]!.GetValue<string>());
        Assert.Equal("cupboard-pi", document["deviceId"]!.GetValue<string>());
        Assert.Equal("2026-10-17T18:00:00.123Z", document["lastSeenAt"]!.GetValue<string>());
        var read = CosmosHubRepository.Read(document);
        Assert.Equal(hub.LastSeenAt, read.LastSeenAt);
        Assert.Equal(hub.OfflineAlertSentAt, read.OfflineAlertSentAt);
        Assert.Equal(hub.LastHeartbeat.Scanners, read.LastHeartbeat.Scanners);
        Assert.Equal(hub.LastHeartbeat.UsbInputDevices, read.LastHeartbeat.UsbInputDevices);
    }

    [Fact]
    public void Absent_scanners_and_alert_stay_absent()
    {
        var hub = new Hub("cupboard-pi", DateTimeOffset.UnixEpoch, null,
            new HeartbeatDetails(DateTimeOffset.UnixEpoch, "abc1234", 0, [], null));

        var document = CosmosHubRepository.Write(hub);

        Assert.False(((System.Text.Json.Nodes.JsonObject)document["lastHeartbeat"]!).ContainsKey("scanners"));
        var read = CosmosHubRepository.Read(document);
        Assert.Null(read.OfflineAlertSentAt);
        Assert.Null(read.LastHeartbeat.Scanners);
    }
}

public sealed class DigestInfrastructureTests
{
    [Fact]
    public void Digest_emails_go_to_the_digest_address()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ACS_ENDPOINT"] = "https://acs.example.com",
            ["EMAIL_SENDER"] = "DoNotReply@example.azurecomm.net",
            ["ALERT_EMAIL_TO"] = "alerts@example.com",
            ["DIGEST_EMAIL_TO"] = "digest@example.com",
        }).Build();

        var options = EmailOptions.FromConfiguration(config);

        Assert.Equal("digest@example.com", options.RecipientFor(NotificationKind.DailyDigest));
        Assert.Equal("alerts@example.com", options.RecipientFor(NotificationKind.OfflineAlert));
    }

    [Fact]
    public void Sent_digest_ids_are_the_date() =>
        Assert.Equal("2026-10-18", CosmosSentDigestRepository.Id(new DateOnly(2026, 10, 18)));

    [Fact]
    public void Digest_rows_read_product_and_barcode()
    {
        var id = Guid.NewGuid();
        var unit = CosmosDigestQueries.ToUnit(new System.Text.Json.Nodes.JsonObject { ["productId"] = id.ToString(), ["barcode"] = null });
        Assert.Equal(new CupboardTracker.Application.Digest.DigestUnit(id, null), unit);
    }
}
