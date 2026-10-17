using System.Text.RegularExpressions;

namespace CupboardTracker.Domain;

/// <summary>Device and hub IDs: lowercase letters, digits and dashes (same rule as the Pi's identity config).</summary>
public static partial class DeviceIds
{
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
    private static partial Regex Pattern();
}
