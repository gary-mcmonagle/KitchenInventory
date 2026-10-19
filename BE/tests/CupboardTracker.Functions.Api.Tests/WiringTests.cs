using CupboardTracker.Functions.Api.Adding;
using CupboardTracker.Functions.Api.Push;
using CupboardTracker.Functions.Api.Recipes;
using CupboardTracker.Functions.Api.ScanFeed;
using CupboardTracker.Functions.Api.Scanners;
using CupboardTracker.Functions.Api.Stock;
using CupboardTracker.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CupboardTracker.Functions.Api.Tests;

/// <summary>Every function's dependencies resolve from the shared registrations (nothing here connects anywhere).</summary>
public sealed class WiringTests
{
    [Theory]
    [InlineData(typeof(AddingFunctions))]
    [InlineData(typeof(ScannersFunctions))]
    [InlineData(typeof(ScanFeedFunctions))]
    [InlineData(typeof(ScanEventsProcessor))]
    [InlineData(typeof(ProductFunctions))]
    [InlineData(typeof(UnitStateFunctions))]
    [InlineData(typeof(ClassificationFunctions))]
    [InlineData(typeof(RecipeFunctions))]
    [InlineData(typeof(PushFunctions))]
    [InlineData(typeof(GenericNameFunctions))]
    [InlineData(typeof(EstimateShelfLivesTimer))]
    [InlineData(typeof(PushJobFunction))]
    [InlineData(typeof(PushExpiryTimer))]
    public async Task Functions_resolve(Type functions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["COSMOS_ENDPOINT"] = "https://cosmos.example.test:443/",
            ["COSMOS_DATABASE"] = "cupboard",
            ["OFF_CONTACT_EMAIL"] = "someone@example.test",
            ["AI_SERVICES_ENDPOINT"] = "https://ais.example.test/",
            ["AzureSignalRConnectionString:serviceUri"] = "https://sigr.example.test",
        }).Build());
        services.AddLogging();
        services.AddCupboardTracker();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.NotNull(ActivatorUtilities.CreateInstance(provider, functions));
    }
}
