using System.Globalization;

namespace CupboardTracker.Application;

public static class Timestamps
{
    /// <summary>ISO 8601 with an explicit offset or Z: a bare local time would be ambiguous.</summary>
    public static bool TryParseWithOffset(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return !string.IsNullOrEmpty(value)
            && (value.EndsWith('Z') || value.EndsWith('z') || HasOffset(value))
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }

    private static bool HasOffset(string value) =>
        value.Length > 6 && (value[^6] is '+' or '-') && value[^3] == ':';
}
