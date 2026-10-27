using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Infrastructure.ShoppingList;
using Azure.Communication.Email;
using Azure.Identity;
using CupboardTracker.Application.AiModels;
using CupboardTracker.Application.Adding;
using CupboardTracker.Application.Digest;
using CupboardTracker.Application.Hubs;
using CupboardTracker.Application.Notifications;
using CupboardTracker.Application.Ping;
using CupboardTracker.Application.GenericNaming;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Push;
using CupboardTracker.Application.RunOut;
using CupboardTracker.Application.SavedRecipes;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Application.Scanners;
using CupboardTracker.Application.Scans;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Application.Stock;
using CupboardTracker.Infrastructure.Cosmos;
using CupboardTracker.Infrastructure.Email;
using CupboardTracker.Infrastructure.GenericNaming;
using CupboardTracker.Infrastructure.Jev;
using CupboardTracker.Infrastructure.Photos;
using CupboardTracker.Infrastructure.ModelTesting;
using CupboardTracker.Infrastructure.Push;
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

        // Which Foundry model each job uses, chosen in the app (#120): the settings document, read at most once a minute
        // per instance, with the app settings as each job's default until a model is made active.
        services.AddSingleton<IAiModelSettingsRepository, CosmosAiModelSettingsRepository>();
        services.AddSingleton<IActiveAiModels, ActiveAiModels>();
        services.AddSingleton(sp => AiModelDefaultsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddTransient<ManageAiModels>();

        // The model test suite (#121): saved checks run when a model is added. A run is a queue message, its progress a
        // short-lived document, its test image a blob, and its price a lookup in Azure's public list prices.
        services.AddSingleton<IModelTestRunRepository, CosmosModelTestRunRepository>();
        services.AddSingleton<IModelTestScheduler, StorageQueueModelTestScheduler>();
        services.AddSingleton<IModelTestImageStore, BlobModelTestImageStore>();
        services.AddSingleton<IModelPrices>(sp =>
            new RetailPriceLookup(new HttpClient(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RetailPriceLookup>>()));
        services.AddSingleton<IModelTestSuite, FoundryModelTestSuite>();
        services.AddSingleton(new ModelTestLimits());
        services.AddSingleton<StartModelTest>(); // holds the per-person hourly limit
        services.AddTransient<GetModelTestRun>();
        services.AddTransient<RunModelTest>();

        // Measuring Cosmos (#115): every call's RUs as App Insights metrics, totals per invocation, index metrics when on.
        services.AddSingleton(sp => CosmosTelemetrySettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ICosmosCallSink>(sp =>
            sp.GetService<Microsoft.ApplicationInsights.TelemetryClient>() is { } telemetry
                ? new AppInsightsCosmosCallSink(telemetry)
                : new NoCosmosCallSink());
        services.AddSingleton<CosmosRecorder>();
        services.AddSingleton<CosmosMetricsHandler>();
        services.AddSingleton(sp => new CosmosInvocationReporter(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CosmosInvocationReporter>>(),
            sp.GetService<Microsoft.ApplicationInsights.TelemetryClient>()));

        // Cosmos over Entra ID: the app's managed identity in Azure, your az login locally. No keys (local auth is off).
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var endpoint = config["COSMOS_ENDPOINT"]
                ?? throw new InvalidOperationException("The COSMOS_ENDPOINT app setting is missing.");
            var client = new CosmosClient(
                endpoint,
                new DefaultAzureCredential(),
                new CosmosClientOptions
                {
                    ApplicationName = "cupboard-tracker",
                    CustomHandlers = { sp.GetRequiredService<CosmosMetricsHandler>() },
                });
            var database = config["COSMOS_DATABASE"]
                ?? throw new InvalidOperationException("The COSMOS_DATABASE app setting is missing.");
            return new CosmosContainers(client, database);
        });
        // Live scan feed (#54): both apps send through SignalR, best-effort; the API app also negotiates and catches up.
        services.AddSingleton(sp => SignalROptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<SignalRScanFeed>();
        services.AddSingleton<IScanFeedPublisher>(sp => sp.GetRequiredService<SignalRScanFeed>());

        // The shopping list (#84): the shoppingList container, the same SignalR hub, and a queue for typed items' aisles.
        services.AddSingleton<IShoppingListRepository, CosmosShoppingListRepository>();
        services.AddSingleton<IShoppingListPublisher>(sp => sp.GetRequiredService<SignalRScanFeed>());
        services.AddSingleton<IListAisleScheduler, StorageQueueListAisleScheduler>();
        services.AddTransient<ShoppingListNotifier>();
        services.AddTransient<AddToShoppingList>();
        services.AddTransient<ChangeShoppingListItem>();
        services.AddSingleton<ISavedRecipeRepository, CosmosSavedRecipeRepository>(); // #105
        services.AddTransient<SavedRecipeUseCases>();
        services.AddTransient<RunOutGroups>(); // #89
        services.AddTransient<AddWhenRunOut>();
        services.AddTransient<SetAddWhenOut>();
        services.AddTransient<GetRunOutStatus>();
        services.AddTransient<GetRunOutChoices>();
        services.AddTransient<ClearDoneShoppingListItems>();
        services.AddTransient<GetShoppingList>();
        services.AddTransient<SuggestListItems>();
        services.AddSingleton(sp => SuggestionCacheSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>())); // #109
        services.AddSingleton<SuggestionIndexCache>();
        services.AddTransient<AssignListAisle>();
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

        // Push notifications (#77, spike #75): our own Web Push sender (aes128gcm + VAPID), subscriptions and "seen"
        // receipts in Cosmos, delayed jobs on a Storage queue. The VAPID private key is a Key Vault reference.
        services.AddSingleton(sp => WebPushOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new PushSettings(sp.GetRequiredService<WebPushOptions>() is { IsConfigured: true } o ? o.PublicKey : null));
        services.AddSingleton(sp => PushTestSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient<IPushSender, WebPushSender>();
        services.AddSingleton<IPushSubscriptionRepository, CosmosPushSubscriptionRepository>();
        services.AddSingleton<IPushSeenRepository, CosmosPushSeenRepository>();
        services.AddSingleton<IPushJobScheduler, StorageQueuePushJobScheduler>();
        services.AddTransient<SendPush>();
        services.AddTransient<SchedulePush>();
        services.AddTransient<RunPushJob>();
        services.AddTransient<RecordPushSeen>();
        services.AddTransient<RegisterPushSubscription>();
        services.AddTransient<RemovePushSubscription>();
        services.AddTransient<SetPushPreferences>();
        services.AddTransient<ExpireStalePushSubscriptions>();
        services.AddSingleton<SendTestPush>(); // holds the per-person hourly limit
        // Stock change push (#80): batches in pushSeen, a delayed job on the stock-push queue.
        services.AddSingleton(sp => StockPushSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IStockChangeBatchRepository, CosmosStockChangeBatchRepository>();
        services.AddSingleton<IStockPushScheduler, StorageQueueStockPushScheduler>();
        services.AddTransient<AddStockChangeToPush>();
        services.AddTransient<SendStockChangePush>();
        // Shopping list push (#86): the list batch in pushSeen, each person's state beside the list, the list-push queue.
        services.AddSingleton(sp => ListPushSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IListPushBatchRepository, CosmosListPushBatchRepository>();
        services.AddSingleton<IListViewerRepository, CosmosListViewerRepository>();
        services.AddSingleton<IListPushScheduler, StorageQueueListPushScheduler>();
        services.AddTransient<AddListItemToPush>();
        services.AddTransient<SendListPush>();
        services.AddTransient<RecordListViewed>();

        // Recipe ideas (#73): gpt-4o on the same Foundry account, with a per-user hourly cost guard.
        services.AddSingleton(sp => RecipeGenerationOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient<IRecipeGenerator, FoundryRecipeGenerator>();
        services.AddSingleton(sp => RecipeSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<RecipeRateLimit>();
        services.AddTransient<SuggestRecipe>();
        services.AddTransient<AdjustRecipe>(); // #104
        services.AddTransient<AdjustIdea>(); // #108: Ready now ideas, the same hourly limit
        services.AddSingleton(sp => RecipeAdditionSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddTransient<IRecipeAdditionRanker, JevRecipeAdditionRanker>();
        services.AddTransient<SuggestRecipeAdditions>();

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
        services.AddSingleton(sp => StillGotThisSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddTransient<MarkStillHere>();
        services.AddTransient<GetExpiredUnits>();
        services.AddSingleton<IStillGotThisPushLog, CosmosStillGotThisPushLog>();
        services.AddTransient<SendStillGotThisPush>();
        services.AddTransient<SetUnitExpiry>();
        services.AddTransient<ChangeUnitState>(); // #65
        services.AddTransient<UndoUnitStateChange>();
        services.AddTransient<OpenNextUnit>();
        services.AddTransient<SetProductAisle>(); // #71
        services.AddTransient<SetProductIcon>(); // #72
        services.AddTransient<SetProductAnimalContent>(); // #112
        services.AddTransient<SetOpenedShelfLife>();
        services.AddSingleton(sp => StockSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));

        // Shelf life and estimated expiry dates (#52): Jev, keyed from Key Vault through an app setting (#42).
        services.AddSingleton(sp => JevOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient<JevClient>();
        services.AddTransient<IShelfLifeEstimator, JevShelfLifeEstimator>();
        services.AddTransient<EstimateShelfLives>();

        // Generic names (#90): Jev matches existing names first; Foundry writes new ones, throttled.
        services.AddSingleton(sp => GenericNameSettingsFromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => GenericNameWriterOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<FoundryThrottle>();
        services.AddSingleton<IGenericNameRepository, CosmosGenericNameRepository>();
        services.AddTransient<IGenericNameMatcher, JevGenericNameMatcher>();
        services.AddHttpClient<IGenericNameWriter, FoundryGenericNameWriter>();
        services.AddTransient<AssignGenericNames>();
        services.AddTransient<ListGenericNames>();
        services.AddTransient<SetProductGenericName>();

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

    /// <summary>PUSH_TEST_PER_HOUR (default 5): test pushes per person per hour (#77).</summary>
    internal static StockPushSettings StockPushSettingsFromConfiguration(IConfiguration config)
    {
        static TimeSpan? Seconds(string? value, int min, int max) =>
            int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? TimeSpan.FromSeconds(n) : null;

        var delay = Seconds(config["STOCK_PUSH_DELAY_SECONDS"], 5, 600) ?? StockPushSettings.Default.Delay;
        var maxWait = Seconds(config["STOCK_PUSH_MAX_WAIT_SECONDS"], 5, 3600) ?? StockPushSettings.Default.MaxWait;
        return new StockPushSettings(delay, maxWait < delay ? delay : maxWait);
    }

    /// <summary>LIST_PUSH_DELAY_SECONDS (default 180), LIST_PUSH_MAX_WAIT_SECONDS (default 900) and
    /// LIST_PUSH_MIN_GAP_MINUTES (default 30), for the shopping list push (#86). Out-of-range values use the default.</summary>
    internal static ListPushSettings ListPushSettingsFromConfiguration(IConfiguration config)
    {
        static int? Number(string? value, int min, int max) =>
            int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n : null;

        var delay = Number(config["LIST_PUSH_DELAY_SECONDS"], 5, 3600) is { } d ? TimeSpan.FromSeconds(d) : ListPushSettings.Default.Delay;
        var maxWait = Number(config["LIST_PUSH_MAX_WAIT_SECONDS"], 5, 7200) is { } w ? TimeSpan.FromSeconds(w) : ListPushSettings.Default.MaxWait;
        var gap = Number(config["LIST_PUSH_MIN_GAP_MINUTES"], 1, 1440) is { } g ? TimeSpan.FromMinutes(g) : ListPushSettings.Default.MinGap;
        return new ListPushSettings(delay, maxWait < delay ? delay : maxWait, gap);
    }

    internal static PushTestSettings PushTestSettingsFromConfiguration(IConfiguration config) =>
        int.TryParse(config["PUSH_TEST_PER_HOUR"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? new PushTestSettings(n)
            : new PushTestSettings();

    /// <summary>GENERIC_NAME_MATCH_MIN_CONFIDENCE (default 0.9) and GENERIC_NAME_BATCH_SIZE (default 254) (#90).</summary>
    internal static GenericNameSettings GenericNameSettingsFromConfiguration(IConfiguration config) => new(
        double.TryParse(config["GENERIC_NAME_MATCH_MIN_CONFIDENCE"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) && c is > 0 and <= 1
            ? c : GenericNameSettings.DefaultMatchMinConfidence,
        int.TryParse(config["GENERIC_NAME_BATCH_SIZE"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var b) && b is > 0 and <= 254
            ? b : GenericNameSettings.DefaultBatchSize);

    /// <summary>SUGGESTION_CACHE_SECONDS (default 60, #109): how long the shopping list's suggestion index is reused.</summary>
    internal static SuggestionCacheSettings SuggestionCacheSettingsFromConfiguration(IConfiguration config) =>
        int.TryParse(config["SUGGESTION_CACHE_SECONDS"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var s) && s is >= 0 and <= 3600
            ? new SuggestionCacheSettings(TimeSpan.FromSeconds(s))
            : SuggestionCacheSettings.Default;

    /// <summary>RECIPE_ADDITION_MIN_CONFIDENCE (default 0.1, #104): below it, Jev's pick isn't suggested.</summary>
    internal static RecipeAdditionSettings RecipeAdditionSettingsFromConfiguration(IConfiguration config) => new(
        double.TryParse(config["RECIPE_ADDITION_MIN_CONFIDENCE"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) && c is >= 0 and <= 1
            ? c : RecipeAdditionSettings.DefaultMinConfidence);

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

    /// <summary>Each job's default deployment (#120): the same settings the callers always read, else gpt-4o.</summary>
    internal static AiModelDefaults AiModelDefaultsFromConfiguration(IConfiguration config) => new(
        RecipeGenerationOptions.FromConfiguration(config).Deployment,
        PhotoRecognitionOptions.FromConfiguration(config).Deployment,
        GenericNameWriterOptions.FromConfiguration(config).Deployment);

    /// <summary>COSMOS_INDEX_METRICS (#115): "true" asks Cosmos for index metrics on queries; anything else is off.</summary>
    internal static CosmosTelemetrySettings CosmosTelemetrySettingsFromConfiguration(IConfiguration config) =>
        new(string.Equals(config["COSMOS_INDEX_METRICS"], "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>STILL_HERE_ASK_AGAIN_DAYS (default 2, #94): how long "Still here" quiets a unit.</summary>
    internal static StillGotThisSettings StillGotThisSettingsFromConfiguration(IConfiguration config)
    {
        var days = StillGotThisSettings.DefaultAskAgainDays;
        if (config["STILL_HERE_ASK_AGAIN_DAYS"] is { Length: > 0 } text
            && (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out days)
                || days is < 1 or > 60))
        {
            throw new InvalidOperationException("STILL_HERE_ASK_AGAIN_DAYS must be a whole number of days, 1 to 60.");
        }

        return new StillGotThisSettings(days);
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
