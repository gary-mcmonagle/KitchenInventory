using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Domain.Tests;

public sealed class EstimatedExpiryTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [Theory]
    // British Summer Time: 23:30 UTC is 00:30 the next day in the UK, so it was bought the next day.
    [InlineData("2026-10-24T23:30:00Z", "2026-10-25")]
    // Clocks went back on 25 Oct 2026: from then on UTC is UK time.
    [InlineData("2026-10-25T00:30:00Z", "2026-10-25")]
    [InlineData("2026-10-26T23:30:00Z", "2026-10-26")]
    // Clocks go forward on 29 Mar 2026.
    [InlineData("2026-03-28T23:30:00Z", "2026-03-28")]
    [InlineData("2026-03-29T23:30:00Z", "2026-03-30")]
    public void The_bought_date_is_the_uk_calendar_date(string checkedInAt, string bought) =>
        Assert.Equal(DateOnly.Parse(bought, System.Globalization.CultureInfo.InvariantCulture),
            EstimatedExpiry.BoughtDate(DateTimeOffset.Parse(checkedInAt, System.Globalization.CultureInfo.InvariantCulture), London));

    [Fact]
    public void Expiry_is_the_bought_date_plus_the_shelf_life()
    {
        var checkedIn = new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero); // 25 Oct in the UK
        Assert.Equal(new DateOnly(2026, 11, 1), EstimatedExpiry.For(checkedIn, 7, London));
        Assert.Equal(new DateOnly(2027, 10, 25), EstimatedExpiry.For(checkedIn, 365, London));
    }

    [Fact]
    public void Days_until_is_negative_once_passed()
    {
        var today = new DateOnly(2026, 10, 18);
        Assert.Equal(-1, EstimatedExpiry.DaysUntil(new DateOnly(2026, 10, 17), today));
        Assert.Equal(0, EstimatedExpiry.DaysUntil(today, today));
        Assert.Equal(14, EstimatedExpiry.DaysUntil(new DateOnly(2026, 11, 1), today));
    }
}
