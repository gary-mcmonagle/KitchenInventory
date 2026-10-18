using Azure.Communication.Email;
using Azure.Identity;
using CupboardTracker.Application.Adding;
using CupboardTracker.Application.Digest;
using CupboardTracker.Application.Hubs;
using CupboardTracker.Application.Notifications;
using CupboardTracker.Application.Ping;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Application.Scanners;
using CupboardTracker.Application.Scans;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Application.Stock;
using CupboardTracker.Infrastructure.Cosmos;
using CupboardTracker.Infrastructure.Email;
using CupboardTracker.Infrastructure.Jev;
using CupboardTracker.Infrastructure.Photos;
using CupboardTracker.Infrastructure.Recipes;
using CupboardTracker.Infrastructure.SignalR;
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
        // Live scan feed (#54): both apps send through SignalR, best-effort; the API app also negotiates and catches up.
        services.AddSingleton(sp => SignalROptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<SignalRScanFeed>();
        services.AddSingleton<IScanFeedPublisher>(sp => sp.GetRequiredService<SignalRScanFeed>());
        services.AddTransient<ScanFeedNotifier>();
        services.AddSingleton<IScanFeedQueries, CosmosScanFeedQueries>();
        services.AddTransient<GetScanFeed>();
        services.AddTransient<DismissFeedItem>();

        // Adding from the app (#58): photo recognition (gpt-4o on Foundry, managed identity), loose products, store labels.
        services.AddSingleton(sp => PhotoRecognitionOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<Azure.Core.TokenCredential>(_ => new DefaultAzureCredential());
        services.AddHttpClient<IProductPhotoRecogniser, FoundryPhotoRecogniser>();
        services.AddSingleton<IStoreLabelRepository, CosmosStoreLabelRepository>();
        services.AddTransient<ProductMatcher>();
        services.AddTransient<RecogniseProductPhoto>();
        services.AddTransient<AddToStock>();
        services.AddTransient<GetLooseProducts>();
        services.AddTransient<StoreLabelCheckInScan>();
        services.AddTransient<AssignStoreLabel>();
        services.AddTransient<DismissStoreLabelFromFeed>();

        // Recipe ideas (#73): gpt-4o on the same Foundry account, with a per-user hourly cost guard.
        services.AddSingleton(sp => RecipeGenerationOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient<IRecipeGenerator, FoundryRecipeGenerator>();
        services.AddSingleton(sp => RecipeSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<RecipeRateLimit>();
        services.AddTransient<SuggestRecipe>();

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
        services.AddTransient<GetProduct>();
        services.AddTransient<NameProduct>();
        services.AddTransient<EditProductDetails>();
        services.AddTransient<SetShelfLife>();
        services.AddTransient<CheckOutUnit>();
        services.AddTransient<SetUnitExpiry>();
        services.AddTransient<ChangeUnitState>(); // #65
        services.AddTransient<UndoUnitStateChange>();
        services.AddTransient<OpenNextUnit>();
        services.AddTransient<SetProductAisle>(); // #71
        services.AddTransient<SetProductIcon>(); // #72
        services.AddTransient<SetOpenedShelfLife>();
        services.AddSingleton(sp => StockSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));

        // Shelf life and estimated expiry dates (#52): Jev, keyed from Key Vault through an app setting (#42).
        services.AddSingleton(sp => JevOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient<JevClient>();
        services.AddTransient<IShelfLifeEstimator, JevShelfLifeEstimator>();
        services.AddTransient<EstimateShelfLives>();

        // Hubs: heartbeats (ingest) and the offline alert (API timer), emailed through ACS with the managed identity.
        // Settings are read lazily, so the ingest app needs none of the email settings.
        services.AddSingleton<IHubRepository, CosmosHubRepository>();
        services.AddTransient<RecordHeartbeat>();
        services.AddSingleton(sp => OfflineAlertSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => EmailOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new EmailClient(sp.GetRequiredService<EmailOptions>().Endpoint, new DefaultAzureCredential()));
        services.AddSingleton<INotifier, AcsEmailNotifier>();
        services.AddTransient<CheckHubsOffline>();

        // Scanners screen (#55).
        services.AddSingleton(sp => ScannersSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IScannersQueries, CosmosScannersQueries>();
        services.AddTransient<GetScanners>();
        services.AddTransient<DismissUnmatchedCheckOut>();

        // The 9pm daily digest (API timer), through the same notifier.
        services.AddSingleton<IDigestQueries, CosmosDigestQueries>();
        services.AddSingleton<ISentDigestRepository, CosmosSentDigestRepository>();
        services.AddSingleton(_ => new DigestSettings(TimeZoneInfo.FindSystemTimeZoneById("Europe/London")));
        services.AddTransient<SendDailyDigest>();

        return services;
    }

    /// <summary>HEARTBEAT_INTERVAL_SECONDS (the Pi's setting, default 120): only shown on the scanners screen.</summary>
    internal static ScannersSettings ScannersSettingsFromConfiguration(IConfiguration config) =>
        new(int.TryParse(config["HEARTBEAT_INTERVAL_SECONDS"], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 10
            ? TimeSpan.FromSeconds(seconds)
            : ScannersSettings.DefaultHeartbeatInterval);

    /// <summary>RECIPE_REQUESTS_PER_HOUR (default 30): the per-user cost guard on recipe suggestions (#73).</summary>
    internal static RecipeSettings RecipeSettingsFromConfiguration(IConfiguration config)
    {
        if (config["RECIPE_REQUESTS_PER_HOUR"] is not { Length: > 0 } text)
        {
            return new RecipeSettings();
        }

        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var perHour)
            || perHour < 1)
        {
            throw new InvalidOperationException("RECIPE_REQUESTS_PER_HOUR must be a whole number, 1 or more.");
        }

        return new RecipeSettings(perHour);
    }

    /// <summary>USE_FIRST_DAYS (default 3) and the household's time zone (#52).</summary>
    internal static StockSettings StockSettingsFromConfiguration(IConfiguration config)
    {
        var days = StockSettings.DefaultUseFirstDays;
        if (config["USE_FIRST_DAYS"] is { Length: > 0 } text
            && (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out days)
                || days > 365))
        {
            throw new InvalidOperationException("USE_FIRST_DAYS must be a whole number of days, 0 to 365.");
        }

        return new StockSettings(TimeZoneInfo.FindSystemTimeZoneById("Europe/London"), days);
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
