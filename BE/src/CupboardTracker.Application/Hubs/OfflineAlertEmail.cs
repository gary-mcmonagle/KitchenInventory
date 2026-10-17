using System.Globalization;
using System.Net;
using CupboardTracker.Application.Notifications;

namespace CupboardTracker.Application.Hubs;

/// <summary>The offline alert email: short and plain, inline styles only, with a plain-text version (spike #39).</summary>
public static class OfflineAlertEmail
{
    public const string Subject = "Cupboard Tracker: Pi offline";

    private static readonly CultureInfo UkCulture = CultureInfo.GetCultureInfo("en-GB");

    public static EmailContent Build(string hubId, DateTimeOffset lastSeenAt, DateTimeOffset now, TimeZoneInfo homeTimeZone)
    {
        var lastSeen = FormatLocal(lastSeenAt, homeTimeZone);
        var silentFor = FormatDuration(now - lastSeenAt);
        const string hint = "Check that it has power and that its Wi-Fi is working.";
        const string scans = "Scans made while its Wi-Fi is down are kept on the Pi and sent when it reconnects, "
            + "but nothing can be scanned while it has no power.";
        const string once = "You'll get one email per outage, and none when it comes back.";

        var plain = $"""
            The Pi ({hubId}) hasn't been in touch for {silentFor}.

            Last seen: {lastSeen}

            {hint} {scans}

            {once}
            """;

        var e = (string text) => WebUtility.HtmlEncode(text);
        var html = $"""
            <div style="font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Arial,sans-serif;font-size:16px;line-height:1.5;color:#1C2A24;max-width:560px">
            <p style="margin:0 0 12px"><strong>The Pi ({e(hubId)}) hasn't been in touch for {e(silentFor)}.</strong></p>
            <p style="margin:0 0 12px">Last seen: {e(lastSeen)}</p>
            <p style="margin:0 0 12px">{e(hint)} {e(scans)}</p>
            <p style="margin:0;color:#5B6B63;font-size:14px">{e(once)}</p>
            </div>
            """;

        return new EmailContent(Subject, html, plain);
    }

    /// <summary>e.g. "Saturday 17 October 2026 at 19:42 BST" (GMT in winter).</summary>
    public static string FormatLocal(DateTimeOffset moment, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(moment, zone);
        var abbreviation = zone.IsDaylightSavingTime(local) ? "BST" : "GMT";
        return local.ToString("dddd d MMMM yyyy 'at' HH:mm", UkCulture) + " " + abbreviation;
    }

    /// <summary>e.g. "23 minutes", "1 hour 5 minutes", "2 days 3 hours".</summary>
    public static string FormatDuration(TimeSpan span)
    {
        static string Unit(int n, string name) => $"{n} {name}{(n == 1 ? "" : "s")}";
        var minutes = Math.Max(0, (int)span.TotalMinutes);
        var (days, hours, mins) = (minutes / 1440, minutes / 60 % 24, minutes % 60);
        return days > 0 ? Unit(days, "day") + (hours > 0 ? " " + Unit(hours, "hour") : "")
            : hours > 0 ? Unit(hours, "hour") + (mins > 0 ? " " + Unit(mins, "minute") : "")
            : Unit(mins, "minute");
    }
}
