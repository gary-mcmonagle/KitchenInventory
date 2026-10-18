using CupboardTracker.Application.ShelfLives;
using Microsoft.Azure.Functions.Worker;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>Every 5 minutes: estimate shelf life for new known products and fill in units' estimated expiry dates
/// (#52). The first runs after deploy do the one-off fill of existing stock, a few products per run.</summary>
public sealed class EstimateShelfLivesTimer(EstimateShelfLives estimate)
{
    [Function("estimateShelfLives")]
    public Task Run([TimerTrigger("0 */5 * * * *", RunOnStartup = false)] TimerInfo timer, CancellationToken cancellationToken) =>
        estimate.ExecuteAsync(cancellationToken);
}
