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

// Cosmos RUs and calls per invocation (#115), outermost so it counts everything the function does.
builder.UseMiddleware<CupboardTracker.Functions.Ingest.CosmosUsageMiddleware>();

builder.Build().Run();
