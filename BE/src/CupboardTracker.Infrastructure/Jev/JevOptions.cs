using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure.Jev;

/// <summary>
/// From app settings: JEV_BASE_URL, JEV_MODEL (pinned, spike #40) and JEV_API_KEY (a Key Vault reference in Azure,
/// #42). <see cref="ApiKey"/> is null until the secret is set and its reference resolves: the platform leaves the
/// reference text itself in the setting until then.
/// </summary>
public sealed record JevOptions(Uri BaseUrl, string Model, string? ApiKey)
{
    public const string DefaultModel = "jev-1.13.0";

    public int Attempts { get; init; } = 3;

    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool IsConfigured => ApiKey is not null;

    public static JevOptions FromConfiguration(IConfiguration config)
    {
        var key = config["JEV_API_KEY"]?.Trim();
        if (string.IsNullOrEmpty(key) || key.StartsWith("@Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase))
        {
            key = null;
        }

        return new JevOptions(
            new Uri(config["JEV_BASE_URL"] is { Length: > 0 } url ? url : "https://api.typesafe.ai"),
            config["JEV_MODEL"] is { Length: > 0 } model ? model : DefaultModel,
            key);
    }

    /// <summary>Never shows the key.</summary>
    public override string ToString() => $"JevOptions {{ BaseUrl = {BaseUrl}, Model = {Model}, Configured = {IsConfigured} }}";
}
