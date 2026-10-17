using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure.OpenFoodFacts;

/// <summary>From app settings: OFF_BASE_URL, OFF_CONTACT_EMAIL (deploy-time, never committed), OFF_MAX_REQUESTS_PER_MINUTE.</summary>
public sealed record OpenFoodFactsOptions(Uri BaseUrl, string ContactEmail, int MaxRequestsPerMinute)
{
    public const int DefaultMaxRequestsPerMinute = 12; // Open Food Facts allows 15/min per IP (spike #28); Gary chose 12

    public int AttemptsPerLookup { get; init; } = 3;

    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The retry timer's share of the budget; check-in processing gets the rest. Together: MaxRequestsPerMinute.</summary>
    public int RetryShare => Math.Max(1, MaxRequestsPerMinute / 5);

    public int CheckInShare => Math.Max(1, MaxRequestsPerMinute - RetryShare);

    public string UserAgent => $"CupboardTracker/1.0 ({ContactEmail})";

    public static OpenFoodFactsOptions FromConfiguration(IConfiguration config)
    {
        var contact = config["OFF_CONTACT_EMAIL"];
        if (string.IsNullOrWhiteSpace(contact))
        {
            throw new InvalidOperationException("The OFF_CONTACT_EMAIL app setting is missing (Open Food Facts needs a contact).");
        }

        var max = int.TryParse(config["OFF_MAX_REQUESTS_PER_MINUTE"], out var parsed) && parsed > 1
            ? parsed
            : DefaultMaxRequestsPerMinute;
        return new OpenFoodFactsOptions(
            new Uri(config["OFF_BASE_URL"] ?? "https://world.openfoodfacts.org"), contact.Trim(), max);
    }
}
