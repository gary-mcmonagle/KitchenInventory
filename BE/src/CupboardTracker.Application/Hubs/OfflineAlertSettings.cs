namespace CupboardTracker.Application.Hubs;

/// <summary>From config: how long a hub may be silent before the offline alert (OFFLINE_THRESHOLD_MINUTES, default 20),
/// and the household's time zone for times shown in emails (Europe/London).</summary>
public sealed record OfflineAlertSettings(TimeSpan Threshold, TimeZoneInfo HomeTimeZone)
{
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMinutes(20);
}
