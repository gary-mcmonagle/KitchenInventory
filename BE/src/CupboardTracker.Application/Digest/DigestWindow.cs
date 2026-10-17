namespace CupboardTracker.Application.Digest;

/// <summary>
/// The daily digest window: 21:00 UK time the day before to 21:00 UK time on <see cref="Date"/> (start inclusive,
/// end exclusive). Computed in the household's time zone, so on clock-change days it's 23 or 25 hours long and
/// nothing is counted twice or missed.
/// </summary>
public sealed record DigestWindow(DateOnly Date, DateTimeOffset Start, DateTimeOffset End)
{
    public const int SendHour = 21;

    public static DigestWindow EndingOn(DateOnly date, TimeZoneInfo zone) =>
        new(date, At(date.AddDays(-1), zone), At(date, zone));

    /// <summary>The timer runs at 20:00 and 21:00 UTC (no time zone support on Linux Flex Consumption); only the run
    /// in the 21:00 hour in the UK sends: 20:00 UTC in BST, 21:00 UTC in GMT.</summary>
    public static bool IsSendTime(DateTimeOffset now, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(now, zone).Hour == SendHour;

    public static DateOnly LocalDate(DateTimeOffset now, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

    private static DateTimeOffset At(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(new TimeOnly(SendHour, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
