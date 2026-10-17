using CupboardTracker.Application.Products;
using Microsoft.Azure.Functions.Worker;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>Every 15 minutes: retry products whose Open Food Facts lookup failed (docs/data-model.md, Lookup failures).</summary>
public sealed class RetryFailedLookupsTimer(RetryFailedLookups retry)
{
    [Function("retryFailedLookups")]
    public Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        retry.ExecuteAsync(cancellationToken);
}
