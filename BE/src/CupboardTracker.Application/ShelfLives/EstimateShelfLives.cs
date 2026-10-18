using CupboardTracker.Application.Products;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.ShelfLives;

/// <summary>What one run did.</summary>
public sealed record ShelfLifeRun(int Estimated, int LowConfidence, int UnitsFilled, bool Stopped);

/// <summary>
/// The shelf-life timer (#52). Each run:
/// <list type="number">
/// <item>estimates the shelf life of known products that don't have one yet, a few per run (new Open Food Facts
/// products, products upgraded by the lookup-retry timer, items named in the app, and on the first runs after
/// deploy, all existing stock);</item>
/// <item>fills in the estimated expiry date of every in-stock unit whose product now has a shelf life.</item>
/// </list>
/// Check-in and naming never wait for it. If the estimator is down, products stay unestimated and the next run tries
/// again. Everything it writes is derived, and re-running changes nothing that's already done.
/// </summary>
public sealed partial class EstimateShelfLives(
    IProductRepository products,
    IUnitRepository units,
    IShelfLifeEstimator estimator,
    StockSettings settings,
    TimeProvider clock,
    ILogger<EstimateShelfLives> logger)
{
    public const int MaxPerRun = 20;

    public async Task<ShelfLifeRun> ExecuteAsync(CancellationToken cancellationToken)
    {
        var (estimated, low, stopped) = await EstimateProductsAsync(cancellationToken);
        var filled = await FillExpiryDatesAsync(cancellationToken);
        if (estimated > 0 || filled > 0 || stopped)
        {
            LogRun(estimated, low, filled, stopped);
        }

        return new ShelfLifeRun(estimated, low, filled, stopped);
    }

    private async Task<(int Estimated, int Low, bool Stopped)> EstimateProductsAsync(CancellationToken cancellationToken)
    {
        var pending = await products.GetNeedingShelfLifeAsync(MaxPerRun, cancellationToken);
        int estimated = 0, low = 0;
        foreach (var product in pending)
        {
            var result = await estimator.EstimateAsync(Describe(product), cancellationToken);
            switch (result)
            {
                case ShelfLifeResult.Estimated { Estimate: var e }:
                    var shelfLife = new ShelfLife(
                        e.Days, e.UsualStorage, e.Confidence, e.LowConfidence, ShelfLife.JevSource, clock.GetUtcNow());
                    if (await products.TrySetShelfLifeAsync(product.WithShelfLife(shelfLife), cancellationToken))
                    {
                        estimated++;
                        low += e.LowConfidence ? 1 : 0;
                        LogEstimated(product.ProductId, product.DisplayName, e.Days, e.UsualStorage, e.Confidence, e.LowConfidence);
                    }

                    break;
                case ShelfLifeResult.NotConfigured:
                    LogNotConfigured();
                    return (estimated, low, true);
                case ShelfLifeResult.Unavailable { Reason: var reason }:
                    LogUnavailable(reason, pending.Count - estimated);
                    return (estimated, low, true);
                case ShelfLifeResult.Failed { Reason: var reason }:
                    // A refused request is a bug or bad configuration (it alerts); the rest would fail the same way.
                    LogFailed(product.ProductId, reason);
                    return (estimated, low, true);
            }
        }

        return (estimated, low, false);
    }

    private async Task<int> FillExpiryDatesAsync(CancellationToken cancellationToken)
    {
        var missing = await units.GetInStockWithoutExpiryAsync(cancellationToken);
        var filled = 0;
        foreach (var group in missing.GroupBy(u => u.Value.ProductId))
        {
            if (await products.GetAsync(group.Key, cancellationToken) is not { ShelfLife: { } shelfLife })
            {
                continue; // unknown, or not estimated yet
            }

            foreach (var unit in group)
            {
                var expiry = EstimatedExpiry.For(unit.Value.CheckedInAt, shelfLife.Days, settings.HomeTimeZone);
                // A conflict means it was just checked out (or filled): either way, nothing more to do for it.
                if (await units.TryReplaceAsync(unit, unit.Value.WithEstimatedExpiry(expiry), cancellationToken))
                {
                    filled++;
                }
            }
        }

        return filled;
    }

    internal static ProductDescription Describe(Product product) =>
        new(product.DisplayName, product.Details?.Brand, product.Details?.Size, product.Details?.CategoryTags ?? []);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Shelf life for {ProductId} ({DisplayName}): {Days} days, {Storage}, confidence {Confidence:0.00}, low confidence {LowConfidence}.")]
    private partial void LogEstimated(Guid productId, string displayName, int days, Storage storage, double confidence, bool lowConfidence);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Shelf-life run: {Estimated} products estimated ({Low} low confidence), {Filled} units given an estimated expiry date, stopped early: {Stopped}.")]
    private partial void LogRun(int estimated, int low, int filled, bool stopped);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Shelf life isn't configured (no Jev key yet): products stay unestimated until it is.")]
    private partial void LogNotConfigured();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Jev couldn't be reached ({Reason}); {Remaining} products stay unestimated and will be retried.")]
    private partial void LogUnavailable(string reason, int remaining);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Jev refused the shelf-life request for {ProductId} ({Reason}); it will be retried, but this needs a fix.")]
    private partial void LogFailed(Guid productId, string reason);
}
