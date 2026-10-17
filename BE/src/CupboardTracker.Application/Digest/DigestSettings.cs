namespace CupboardTracker.Application.Digest;

/// <summary>The household's time zone (Europe/London) for the digest window and dates.</summary>
public sealed record DigestSettings(TimeZoneInfo HomeTimeZone);
