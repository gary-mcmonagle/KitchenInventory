using Azure.Communication.Email;
using Azure.Identity;
using CupboardTracker.Application.Digest;
using CupboardTracker.Application.Hubs;
using CupboardTracker.Application.Notifications;
using CupboardTracker.Application.Ping;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Scans;
using CupboardTracker.Application.Stock;
using CupboardTracker.Infrastructure.Cosmos;
using CupboardTracker.Infrastructure.Email;
using CupboardTracker.Infrastructure.OpenFoodFacts;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CupboardTracker.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the use cases and their infrastructure implementations shared by both Function apps.</summary>
    public static IServiceCollection AddCupboardTracker(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IDeploymentInfo, ConfigurationDeploymentInfo>();
        services.AddTransient<GetPingStatus>();

        // Cosmos over Entra ID: the app's managed identity in Azure, your az login locally. No keys (local auth is off).
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var endpoint = config["COSMOS_ENDPOINT"]
                ?? throw new InvalidOperationException("The COSMOS_ENDPOINT app setting is missing.");
            var client = new CosmosClient(
                endpoint,
                new DefaultAzureCredential(),
                new CosmosClientOptions { ApplicationName = "cupboard-tracker" });
            var database = config["COSMOS_DATABASE"]
                ?? throw new InvalidOperationException("The COSMOS_DATABASE app setting is missing.");
            return new CosmosContainers(client, database);
        });
        services.AddSingleton<IScanEventRepository, CosmosScanEventRepository>();
        services.AddSingleton<IDeviceRepository, CosmosDeviceRepository>();
        services.AddTransient<RecordScan>();

        // Stock: check-in, check-out, and product lookup through Open Food Facts (options read lazily, so the
        // ingest app, which never looks products up, doesn't need the OFF settings).
        services.AddSingleton<IProductRepository, CosmosProductRepository>();
        services.AddSingleton<IUnitRepository, CosmosUnitRepository>();
        services.AddSingleton<IUnmatchedCheckOutRepository, CosmosUnmatchedCheckOutRepository>();
        services.AddSingleton(sp => OpenFoodFactsOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<OpenFoodFactsThrottles>();
        services.AddHttpClient<IProductLookup, OpenFoodFactsProductLookup>();
        services.AddTransient<ProductResolver>();
        services.AddTransient<CheckInScan>();
        services.AddTransient<CheckOutScan>();
        services.AddTransient<ProcessScanEvent>();
        services.AddTransient<RetryFailedLookups>();
        services.AddSingleton<IStockQueries, CosmosStockQueries>();
        services.AddTransient<GetStock>();
        services.AddTransient<CheckOutFromApp>();
        services.AddTransient<UndoCheckOut>();

        // Hubs: heartbeats (ingest) and the offline alert (API timer), emailed through ACS with the managed identity.
        // Settings are read lazily, so the ingest app needs none of the email settings.
        services.AddSingleton<IHubRepository, CosmosHubRepository>();
        services.AddTransient<RecordHeartbeat>();
        services.AddSingleton(sp => OfflineAlertSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => EmailOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new EmailClient(sp.GetRequiredService<EmailOptions>().Endpoint, new DefaultAzureCredential()));
        services.AddSingleton<INotifier, AcsEmailNotifier>();
        services.AddTransient<CheckHubsOffline>();

        // The 9pm daily digest (API timer), through the same notifier.
        services.AddSingleton<IDigestQueries, CosmosDigestQueries>();
        services.AddSingleton<ISentDigestRepository, CosmosSentDigestRepository>();
        services.AddSingleton(_ => new DigestSettings(TimeZoneInfo.FindSystemTimeZoneById("Europe/London")));
        services.AddTransient<SendDailyDigest>();

        return services;
    }

    /// <summary>OFFLINE_THRESHOLD_MINUTES (default 20) and the household's time zone, resolved now so a missing zone
    /// fails loudly.</summary>
    internal static OfflineAlertSettings OfflineAlertSettingsFromConfiguration(IConfiguration config)
    {
        var threshold = OfflineAlertSettings.DefaultThreshold;
        if (config["OFFLINE_THRESHOLD_MINUTES"] is { Length: > 0 } text)
        {
            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutes)
                || minutes < 1)
            {
                throw new InvalidOperationException("OFFLINE_THRESHOLD_MINUTES must be a whole number of minutes, 1 or more.");
            }

            threshold = TimeSpan.FromMinutes(minutes);
        }

        return new OfflineAlertSettings(threshold, TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
    }
}
