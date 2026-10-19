using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Push;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Push;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class PushFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("Config")]
    [InlineData("Put")]
    [InlineData("Delete")]
    [InlineData("Preferences")]
    [InlineData("Seen")]
    [InlineData("Test")]
    public void Every_push_endpoint_needs_a_signed_in_user(string method) =>
        Assert.True(FunctionAuthPolicy.RequiresUser($"CupboardTracker.Functions.Api.Push.PushFunctions.{method}"));

    [Fact]
    public void The_queue_job_and_timer_arent_http() =>
        Assert.False(FunctionAuthPolicy.RequiresUser("CupboardTracker.Functions.Api.Push.PushJobFunction.Run"));

    [Fact]
    public void Results_map_to_statuses()
    {
        var subscription = new PushSubscription("id", Guid.NewGuid(), "https://web.push.apple.com/x", "p", "a", "iPhone",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, new Dictionary<string, bool> { ["stockChanges"] = true });
        var (status, body) = PushFunctions.ToResponse(new PushSubscriptionResult.Saved(subscription));
        Assert.Equal(HttpStatusCode.OK, status);
        var json = JsonSerializer.Serialize(body, WebJson);
        Assert.Equal("""{"id":"id","deviceLabel":"iPhone","preferences":{"stockChanges":true}}""", json);
        Assert.DoesNotContain("web.push.apple.com", json, StringComparison.Ordinal); // the endpoint never goes back out

        Assert.Equal(HttpStatusCode.OK, PushFunctions.ToResponse(new PushSubscriptionResult.Removed()).Status);
        Assert.Equal(HttpStatusCode.BadRequest, PushFunctions.ToResponse(new PushSubscriptionResult.Invalid("x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, PushFunctions.ToResponse(new PushSubscriptionResult.NotFound()).Status);
    }

    [Fact]
    public void Reads_nested_strings_from_the_body()
    {
        using var document = JsonDocument.Parse("""{"endpoint":"e","keys":{"p256dh":"p","auth":5}}""");
        Assert.Equal("e", PushFunctions.Text(document.RootElement, "endpoint"));
        Assert.Equal("p", PushFunctions.Text(document.RootElement, "keys", "p256dh"));
        Assert.Null(PushFunctions.Text(document.RootElement, "keys", "auth"));
        Assert.Null(PushFunctions.Text(document.RootElement, "missing", "x"));
    }
}
