using System.Net;
using System.Text;
using CupboardTracker.Application.Hubs;
using CupboardTracker.Functions.Ingest.Heartbeats;

namespace CupboardTracker.Functions.Ingest.Tests;

public sealed class HeartbeatRequestTests
{
    private static (RecordHeartbeatCommand? Command, string? Error) Parse(string json) =>
        HeartbeatRequest.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parses_a_heartbeat_with_scanners()
    {
        var (command, error) = Parse("""
            {"hubId":"cupboard-pi","sentAt":"2026-10-17T18:00:00.000Z","clientSha":"abc1234","uptimeSeconds":3600,
             "usbInputDevices":["USB Adapter USB Device"],
             "scanners":[{"deviceId":"scanner-in","present":true},{"deviceId":"scanner-out","present":false}],
             "somethingNew":1}
            """);

        Assert.Null(error);
        Assert.Equal("cupboard-pi", command!.HubId);
        Assert.Equal(3600, command.UptimeSeconds);
        Assert.Equal(["USB Adapter USB Device"], command.UsbInputDevices);
        Assert.Equal([new ScannerPresenceCommand("scanner-in", true), new ScannerPresenceCommand("scanner-out", false)], command.Scanners);
    }

    [Theory]
    [InlineData("""{"hubId":"cupboard-pi"}""")]
    [InlineData("""{"hubId":"cupboard-pi","scanners":null}""")]
    public void Scanners_are_optional(string json) => Assert.Null(Parse(json).Command!.Scanners);

    [Theory]
    [InlineData("""{"hubId":5}""", "hubId must be a string.")]
    [InlineData("""{"uptimeSeconds":"60"}""", "uptimeSeconds must be a whole number.")]
    [InlineData("""{"uptimeSeconds":1.5}""", "uptimeSeconds must be a whole number.")]
    [InlineData("""{"usbInputDevices":"kbd"}""", "usbInputDevices must be a list of strings.")]
    [InlineData("""{"usbInputDevices":[1]}""", "usbInputDevices must be a list of strings.")]
    [InlineData("""{"scanners":{"deviceId":"x"}}""", "scanners must be a list of { deviceId, present } objects.")]
    [InlineData("[]", "Body must be a JSON object.")]
    [InlineData("nope", "Body must be a JSON object.")]
    public void Wrong_json_types_are_rejected(string json, string expected) => Assert.Equal(expected, Parse(json).Error);

    [Fact]
    public void A_scanner_with_a_non_boolean_present_is_left_for_validation() =>
        Assert.Null(Parse("""{"scanners":[{"deviceId":"scanner-in","present":"yes"}]}""").Command!.Scanners![0].Present);

    [Fact]
    public void Responses()
    {
        Assert.Equal(HttpStatusCode.Accepted, HeartbeatRequest.ToResponse(new RecordHeartbeatResult.Recorded("cupboard-pi")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, HeartbeatRequest.ToResponse(new RecordHeartbeatResult.Invalid(["x"])).Status);
    }
}
