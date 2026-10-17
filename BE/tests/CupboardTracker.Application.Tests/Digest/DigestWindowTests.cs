using CupboardTracker.Application.Digest;

namespace CupboardTracker.Application.Tests.Digest;

public sealed class DigestWindowTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static DateTimeOffset Utc(int y, int m, int d, int h) => new(y, m, d, h, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2026, 7, 15, 20, true)]   // BST: 20:00 UTC is 21:00 in the UK
    [InlineData(2026, 7, 15, 21, false)]  // BST: 21:00 UTC is 22:00
    [InlineData(2026, 12, 15, 20, false)] // GMT: 20:00 UTC is 20:00
    [InlineData(2026, 12, 15, 21, true)]  // GMT: 21:00 UTC is 21:00
    [InlineData(2026, 3, 29, 20, true)]   // last Sunday of March: BST from 01:00 UTC
    [InlineData(2026, 3, 29, 21, false)]
    [InlineData(2026, 10, 25, 20, false)] // last Sunday of October: GMT from 01:00 UTC
    [InlineData(2026, 10, 25, 21, true)]
    public void Only_the_run_at_9pm_uk_time_sends(int y, int m, int d, int utcHour, bool sends) =>
        Assert.Equal(sends, DigestWindow.IsSendTime(Utc(y, m, d, utcHour), London));

    [Fact]
    public void A_normal_day_is_24_hours_from_9pm_to_9pm()
    {
        var window = DigestWindow.EndingOn(new DateOnly(2026, 10, 17), London);
        Assert.Equal(Utc(2026, 10, 16, 20), window.Start);
        Assert.Equal(Utc(2026, 10, 17, 20), window.End);
    }

    [Fact]
    public void The_day_the_clocks_go_forward_is_23_hours()
    {
        var window = DigestWindow.EndingOn(new DateOnly(2026, 3, 29), London);
        Assert.Equal(Utc(2026, 3, 28, 21), window.Start);
        Assert.Equal(Utc(2026, 3, 29, 20), window.End);
        Assert.Equal(TimeSpan.FromHours(23), window.End - window.Start);
    }

    [Fact]
    public void The_day_the_clocks_go_back_is_25_hours()
    {
        var window = DigestWindow.EndingOn(new DateOnly(2026, 10, 25), London);
        Assert.Equal(Utc(2026, 10, 24, 20), window.Start);
        Assert.Equal(Utc(2026, 10, 25, 21), window.End);
        Assert.Equal(TimeSpan.FromHours(25), window.End - window.Start);
    }

    [Fact]
    public void Consecutive_windows_meet_exactly()
    {
        for (var day = new DateOnly(2026, 3, 27); day < new DateOnly(2026, 11, 1); day = day.AddDays(1))
        {
            Assert.Equal(DigestWindow.EndingOn(day, London).End, DigestWindow.EndingOn(day.AddDays(1), London).Start);
        }
    }
}
