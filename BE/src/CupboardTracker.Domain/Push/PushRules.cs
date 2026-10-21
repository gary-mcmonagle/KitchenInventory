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
/// Apple reads it as **URL-safe Base64**, so it refuses (400 BadWebPushTopic) anything that can't be Base64: more than
/// 32 characters, or a length of 1, 5, 9, 13… (length % 4 == 1, which no Base64 string has). That's why "stock" (5)
/// and spike #75's "poc-use-first" (13) were refused while "test" (4) and "pocUseFirst" (11) were delivered (#106).
/// We also keep to letters and digits.
/// </summary>
public static partial class PushTopic
{
    public const int MaxLength = 32;

    public static bool IsValid(string topic) => IsName(topic) && topic.Length % 4 != 1;

    /// <summary>Letters and digits, 1–32: the shape of a topic, without Apple's Base64 length rule (preference names).</summary>
    public static bool IsName(string text) => text.Length is > 0 and <= MaxLength && Allowed().IsMatch(text);

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex Allowed();
}
