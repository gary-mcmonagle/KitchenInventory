using System.Text.RegularExpressions;

namespace CupboardTracker.Domain.Push;

/// <summary>No pushes at night (#77): 21:30 to 07:30 in the household's time zone (UK time, BST or GMT).</summary>
public static class QuietHours
{
    public static readonly TimeOnly Start = new(21, 30);
    public static readonly TimeOnly End = new(7, 30);

    public static bool IsQuiet(DateTimeOffset at, TimeZoneInfo homeTimeZone)
    {
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, homeTimeZone).DateTime);
        return local >= Start || local < End;
    }
}

/// <summary>
/// Push subscriptions are only accepted and sent to on the browsers' own push services (spike #75), never arbitrary
/// URLs: Apple (Safari, iPhone), Google FCM (Chrome, Edge on Android), Mozilla (Firefox) and Microsoft WNS (Edge).
/// </summary>
public static class PushServices
{
    private static readonly string[] Hosts = ["web.push.apple.com", "fcm.googleapis.com", "updates.push.services.mozilla.com"];
    private const string MicrosoftSuffix = ".notify.windows.com";

    public static bool IsAllowed(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && (Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || uri.Host.EndsWith(MicrosoftSuffix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The <c>Topic</c> (and notification tag) for a kind of push: a newer push with the same topic replaces an older one.
/// Apple only accepts letters and digits, at most 32 (it rejects hyphens with 400 BadWebPushTopic, spike #75).
/// </summary>
public static partial class PushTopic
{
    public const int MaxLength = 32;

    public static bool IsValid(string topic) => topic.Length is > 0 and <= MaxLength && Allowed().IsMatch(topic);

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex Allowed();
}
