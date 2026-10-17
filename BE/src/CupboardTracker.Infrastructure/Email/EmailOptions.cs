using CupboardTracker.Application.Notifications;
using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure.Email;

/// <summary>ACS Email settings (no secrets: the app's managed identity sends). Recipients come from config per kind, so
/// use cases never handle addresses (spike #39).</summary>
public sealed record EmailOptions(Uri Endpoint, string Sender, IReadOnlyDictionary<NotificationKind, string> Recipients)
{
    public static EmailOptions FromConfiguration(IConfiguration config)
    {
        var endpoint = Required(config, "ACS_ENDPOINT");
        var recipients = new Dictionary<NotificationKind, string>();
        if (config["ALERT_EMAIL_TO"] is { Length: > 0 } alertTo)
        {
            recipients[NotificationKind.OfflineAlert] = alertTo.Trim();
        }

        return new EmailOptions(new Uri(endpoint), Required(config, "EMAIL_SENDER"), recipients);
    }

    /// <summary>The address for a kind of notification. Throws (naming the setting, not an address) if it isn't set.</summary>
    public string RecipientFor(NotificationKind kind) =>
        Recipients.TryGetValue(kind, out var address)
            ? address
            : throw new InvalidOperationException($"No recipient is configured for {kind} emails ({SettingFor(kind)}).");

    public static string SettingFor(NotificationKind kind) => kind switch
    {
        NotificationKind.OfflineAlert => "ALERT_EMAIL_TO",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Required(IConfiguration config, string name) =>
        config[name] is { Length: > 0 } value
            ? value.Trim()
            : throw new InvalidOperationException($"The {name} app setting is missing.");
}
