using System.Net;
using System.Text.Json;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Domain;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.ScanFeed;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class ScanFeedFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid Scan = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Product = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset At = new(2026, 10, 18, 7, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("CupboardTracker.Functions.Api.ScanFeed.ScanFeedFunctions.Negotiate")]
    [InlineData("CupboardTracker.Functions.Api.ScanFeed.ScanFeedFunctions.Get")]
    [InlineData("CupboardTracker.Functions.Api.ScanFeed.ScanFeedFunctions.Dismiss")]
    public void All_need_a_signed_in_user(string entryPoint) => Assert.True(FunctionAuthPolicy.RequiresUser(entryPoint));

    [Fact]
    public void Catch_up_json_has_each_events_type_and_payload()
    {
        var view = new ScanFeedView(At, [
            new ScanReceived(Scan, "scanner-in", Role.In, At, "5000354922831", At.AddSeconds(-1)),
            new FeedDismissed(Product, At),
        ]);

        Assert.Equal(
            """{"generatedAt":"2026-10-18T07:30:00.000Z","events":[""" +
            """{"type":"scanReceived","scanId":"11111111-1111-1111-1111-111111111111","deviceId":"scanner-in","role":"in","occurredAt":"2026-10-18T07:30:00.000Z","barcode":"5000354922831","scannedAt":"2026-10-18T07:29:59.000Z"},""" +
            """{"type":"feedDismissed","occurredAt":"2026-10-18T07:30:00.000Z","productId":"22222222-2222-2222-2222-222222222222"}]}""",
            JsonSerializer.Serialize(ScanFeedFunctions.ToJson(view), WebJson));
    }

    [Fact]
    public void Dismiss_responses()
    {
        var (status, body) = ScanFeedFunctions.ToResponse(new DismissFeedItemResult.Dismissed(Product, At));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"productId":"22222222-2222-2222-2222-222222222222","feedDismissedAt":"2026-10-18T07:30:00.000Z"}""",
            JsonSerializer.Serialize(body, WebJson));
        Assert.Equal(HttpStatusCode.NotFound, ScanFeedFunctions.ToResponse(new DismissFeedItemResult.NotFound(Product)).Status);
    }
}
