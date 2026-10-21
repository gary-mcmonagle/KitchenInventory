using CupboardTracker.Domain.Push;

namespace CupboardTracker.Domain.Tests;

public sealed class PushRulesTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [Theory]
    // October, BST (UTC+1): 21:30 UK is 20:30 UTC, 07:30 UK is 06:30 UTC.
    [InlineData("2026-10-18T20:29:59Z", false)]
    [InlineData("2026-10-18T20:30:00Z", true)]
    [InlineData("2026-10-19T02:00:00Z", true)]
    [InlineData("2026-10-19T06:29:59Z", true)]
    [InlineData("2026-10-19T06:30:00Z", false)]
    // December, GMT (UTC+0): the same UK times are an hour later in UTC.
    [InlineData("2026-12-01T21:29:59Z", false)]
    [InlineData("2026-12-01T21:30:00Z", true)]
    [InlineData("2026-12-02T07:29:59Z", true)]
    [InlineData("2026-12-02T07:30:00Z", false)]
    public void Quiet_hours_are_2130_to_0730_uk_time(string utc, bool quiet) =>
        Assert.Equal(quiet, QuietHours.IsQuiet(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), London));

    [Theory]
    [InlineData("https://web.push.apple.com/QGuQyavXutnMH_abc", true)]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc:def", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=abc", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc", false)]
    [InlineData("https://user:pass@fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://evil.example/fcm.googleapis.com", false)]
    [InlineData("https://fcm.googleapis.com.evil.example/x", false)]
    [InlineData("https://notify.windows.com.evil.example/x", false)]
    [InlineData("not a url", false)]
    public void Only_the_browsers_push_services_are_allowed(string endpoint, bool allowed) =>
        Assert.Equal(allowed, PushServices.IsAllowed(endpoint));

    [Theory]
    [InlineData("test", true)] // delivered to an iPhone
    [InlineData("pocUseFirst", true)] // delivered to an iPhone (spike #75)
    [InlineData("stockChanges", true)]
    [InlineData("stock", false)] // Apple: 400 BadWebPushTopic (#106): 5 characters can't be Base64
    [InlineData("poc-use-first", false)] // Apple: 400 BadWebPushTopic (spike #75): 13 characters
    [InlineData("useFirst2", false)] // 9
    [InlineData("a", false)]
    [InlineData("use-first", false)]
    [InlineData("use_first", false)]
    [InlineData("", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)] // 33
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", true)] // 32
    public void Topics_are_letters_and_digits_up_to_32_with_a_length_Base64_can_have(string topic, bool valid) =>
        Assert.Equal(valid, PushTopic.IsValid(topic));
}
