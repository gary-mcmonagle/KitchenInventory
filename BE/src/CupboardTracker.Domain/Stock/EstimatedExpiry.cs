namespace CupboardTracker.Domain.Stock;

/// <summary>Estimated expiry dates (#52): all dates are estimates, never read from the pack.</summary>
public static class EstimatedExpiry
{
    /// <summary>The bought date (when the unit was checked in, as a calendar date in the household's time zone) plus
    /// the product's shelf life.</summary>
    public static DateOnly For(DateTimeOffset checkedInAt, int shelfLifeDays, TimeZoneInfo homeTimeZone) =>
        BoughtDate(checkedInAt, homeTimeZone).AddDays(shelfLifeDays);

    public static DateOnly BoughtDate(DateTimeOffset checkedInAt, TimeZoneInfo homeTimeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(checkedInAt, homeTimeZone).DateTime);

    /// <summary>Days from <paramref name="today"/> to <paramref name="expiry"/>: negative once it's passed, 0 on the day.</summary>
    public static int DaysUntil(DateOnly expiry, DateOnly today) => expiry.DayNumber - today.DayNumber;
}
