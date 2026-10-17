using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Infrastructure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// The Application Insights SDK adds a default filter that only forwards Warning and above from the worker.
// Remove it, so Information logs (check-ins, lookups, replays) reach App Insights; host.json still controls levels.
builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
{
    var defaultRule = options.Rules.FirstOrDefault(rule =>
        rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }
});

builder.Services.AddCupboardTracker();

// Signed-in users (#36): bearer tokens checked in code for every HTTP function not marked [PublicEndpoint].
builder.UseMiddleware<BearerAuthMiddleware>();
builder.Services.AddSingleton(sp => EntraOptions.FromConfiguration(sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
builder.Services.AddSingleton<ITokenValidator>(sp =>
{
    var options = sp.GetRequiredService<EntraOptions>();
    return new EntraTokenValidator(options, EntraTokenValidator.MetadataFor(options));
});

builder.Build().Run();
