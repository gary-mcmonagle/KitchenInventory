using CupboardTracker.Infrastructure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace CupboardTracker.Functions.Api;

/// <summary>
/// Totals each invocation's Cosmos RUs and calls (#115): an HTTP request, or a timer, queue or change-feed run. Reported
/// as metrics by function and as one trace correlated with the request (its operation id), after the function ends.
/// </summary>
public sealed class CosmosUsageMiddleware(CosmosInvocationReporter reporter) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        using var scope = CosmosUsage.Begin();
        try
        {
            await next(context);
        }
        finally
        {
            reporter.Report(context.FunctionDefinition.Name, TriggerOf(context), scope.Usage);
        }
    }

    /// <summary>"httpTrigger" → "http", "timerTrigger" → "timer", …</summary>
    internal static string TriggerOf(FunctionContext context) =>
        context.FunctionDefinition.InputBindings.Values
            .Select(b => b.Type)
            .FirstOrDefault(t => t.EndsWith("Trigger", StringComparison.OrdinalIgnoreCase)) is { } type
            ? type[..^"Trigger".Length]
            : "other";
}
