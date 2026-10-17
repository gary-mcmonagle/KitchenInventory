using Azure.Identity;
using CupboardTracker.Application.Ping;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Scans;
using CupboardTracker.Application.Stock;
using CupboardTracker.Infrastructure.Cosmos;
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

        return services;
    }
}
