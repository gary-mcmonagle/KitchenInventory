namespace CupboardTracker.Domain;

/// <summary>What a device's scans mean: check-in or check-out.</summary>
public enum Role
{
    In,
    Out,
}

/// <summary>The wire names for <see cref="Role"/>: <c>in</c> and <c>out</c>.</summary>
public static class RoleNames
{
    public const string In = "in";
    public const string Out = "out";

    public static string ToWireName(this Role role) => role switch
    {
        Role.In => In,
        Role.Out => Out,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };

    public static bool TryParse(string? value, out Role role)
    {
        switch (value)
        {
            case In:
                role = Role.In;
                return true;
            case Out:
                role = Role.Out;
                return true;
            default:
                role = default;
                return false;
        }
    }
}
