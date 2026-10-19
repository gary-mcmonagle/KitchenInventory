using CupboardTracker.Application.GenericNaming;
using CupboardTracker.Application.ShelfLives;
using Microsoft.Azure.Functions.Worker;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>Every 5 minutes: estimate shelf life for new known products and fill in units' estimated expiry dates
/// (#52), then give products with an aisle a generic name (#90). The first runs after deploy do the one-off fills of
/// existing stock, a few products per run.</summary>
public sealed class EstimateShelfLivesTimer(EstimateShelfLives estimate, AssignGenericNames genericNames)
{
    [Function("estimateShelfLives")]
    public async Task Run([TimerTrigger("0 */5 * * * *", RunOnStartup = false)] TimerInfo timer, CancellationToken cancellationToken)
    {
        await estimate.ExecuteAsync(cancellationToken);
        await genericNames.ExecuteAsync(cancellationToken);
    }
}
