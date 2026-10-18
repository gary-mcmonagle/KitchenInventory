namespace CupboardTracker.Application.Stock;

/// <summary>
/// The household's time zone (Europe/London: bought dates and "today" are UK calendar dates) and how close an
/// estimated expiry must be for a product to be in Use first (USE_FIRST_DAYS, default 3; #52).
/// </summary>
public sealed record StockSettings(TimeZoneInfo HomeTimeZone, int UseFirstDays = StockSettings.DefaultUseFirstDays)
{
    public const int DefaultUseFirstDays = 3;

    public DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, HomeTimeZone).DateTime);

    /// <summary>Use first: expired, or due within <see cref="UseFirstDays"/> days.</summary>
    public bool IsUseFirst(int? daysUntilSoonest) => daysUntilSoonest is { } days && days <= UseFirstDays;
}
