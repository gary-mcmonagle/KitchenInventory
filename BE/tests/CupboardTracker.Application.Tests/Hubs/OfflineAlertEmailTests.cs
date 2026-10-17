using CupboardTracker.Application.Hubs;

namespace CupboardTracker.Application.Tests.Hubs;

public sealed class OfflineAlertEmailTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [Fact]
    public void Summer_times_are_shown_in_bst()
    {
        var lastSeen = new DateTimeOffset(2026, 10, 17, 18, 42, 0, TimeSpan.Zero);
        Assert.Equal("Saturday 17 October 2026 at 19:42 BST", OfflineAlertEmail.FormatLocal(lastSeen, London));
    }

    [Fact]
    public void Winter_times_are_shown_in_gmt_after_the_clocks_go_back()
    {
        // Clocks go back at 02:00 BST (01:00 UTC) on Sunday 25 October 2026.
        Assert.Equal("Sunday 25 October 2026 at 01:59 BST",
            OfflineAlertEmail.FormatLocal(new DateTimeOffset(2026, 10, 25, 0, 59, 0, TimeSpan.Zero), London));
        Assert.Equal("Sunday 25 October 2026 at 01:00 GMT",
            OfflineAlertEmail.FormatLocal(new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero), London));
    }

    [Fact]
    public void Clocks_going_forward_in_march()
    {
        // 01:00 GMT on Sunday 29 March 2026 becomes 02:00 BST.
        Assert.Equal("Sunday 29 March 2026 at 00:59 GMT",
            OfflineAlertEmail.FormatLocal(new DateTimeOffset(2026, 3, 29, 0, 59, 0, TimeSpan.Zero), London));
        Assert.Equal("Sunday 29 March 2026 at 02:00 BST",
            OfflineAlertEmail.FormatLocal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), London));
    }

    [Theory]
    [InlineData(21, "21 minutes")]
    [InlineData(1, "1 minute")]
    [InlineData(60, "1 hour")]
    [InlineData(65, "1 hour 5 minutes")]
    [InlineData(1441, "1 day")]
    [InlineData(3060, "2 days 3 hours")]
    public void Durations_read_naturally(int minutes, string expected) =>
        Assert.Equal(expected, OfflineAlertEmail.FormatDuration(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void The_email_has_the_details_in_both_versions()
    {
        var lastSeen = new DateTimeOffset(2026, 10, 17, 18, 42, 0, TimeSpan.Zero);
        var email = OfflineAlertEmail.Build("cupboard-pi", lastSeen, lastSeen.AddMinutes(23), London);

        Assert.Equal("Cupboard Tracker: Pi offline", email.Subject);
        foreach (var body in new[] { email.PlainText, email.Html })
        {
            Assert.Contains("cupboard-pi", body, StringComparison.Ordinal);
            Assert.Contains("23 minutes", body, StringComparison.Ordinal);
            Assert.Contains("Saturday 17 October 2026 at 19:42 BST", body, StringComparison.Ordinal);
            Assert.Contains("power", body, StringComparison.Ordinal);
            Assert.Contains("kept on the Pi", body, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("<", email.PlainText, StringComparison.Ordinal);
        Assert.Contains("style=", email.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_is_encoded()
    {
        var at = new DateTimeOffset(2026, 10, 17, 18, 42, 0, TimeSpan.Zero);
        var email = OfflineAlertEmail.Build("<b>&\"pi\"", at, at.AddMinutes(30), London);

        Assert.Contains("&lt;b&gt;&amp;&quot;pi&quot;", email.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>&", email.Html, StringComparison.Ordinal);
        Assert.Contains("<b>&\"pi\"", email.PlainText, StringComparison.Ordinal); // plain text is as-is
    }
}
