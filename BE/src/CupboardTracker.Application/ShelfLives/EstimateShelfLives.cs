using CupboardTracker.Application.Products;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.ShelfLives;

/// <summary>What one run did.</summary>
public sealed record ShelfLifeRun(
    int Estimated, int LowConfidence, int UnitsFilled, bool Stopped, int StatesEstimated = 0, int PendingFilled = 0, int Classified = 0,
    int AnimalContentSet = 0);

/// <summary>
/// The shelf-life timer (#52). Each run:
/// <list type="number">
/// <item>estimates the shelf life of known products that don't have one yet, a few per run (new Open Food Facts
/// products, products upgraded by the lookup-retry timer, items named in the app, and on the first runs after
/// deploy, all existing stock);</item>
/// <item>fills in the estimated expiry date of every in-stock unit whose product now has a shelf life;</item>
/// <item>asks the opened / frozen / defrosted questions for products that have a shelf life but no state estimates
/// (#65), a few per run: new products shortly after their shelf life, and on the first runs after deploy, all
/// existing stock;</item>
/// <item>fills in units opened, frozen or defrosted before their product had that estimate;</item>
/// <item>gives known products an aisle and an icon (#71, #72), in one request each, a few per run;</item>
/// <item>marks known products with an aisle as meat, fish or neither (#112): the Meat &amp; fish aisle asks Jev only
/// which of the two; otherwise decisive Open Food Facts categories answer without Jev; otherwise Jev's three-way
/// choice, stored as unknown below <see cref="AnimalContents.MinConfidence"/>. The first runs after deploy are the
/// backfill, a few products per run.</item>
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
    /// <summary>Products per step per run. 40 so a stock take of a couple of hundred products is filled in within
    /// the evening (a run takes a few minutes at most; timer runs never overlap).</summary>
    public const int MaxPerRun = 40;

    public async Task<ShelfLifeRun> ExecuteAsync(CancellationToken cancellationToken)
    {
        var (estimated, low, stopped) = await EstimateProductsAsync(cancellationToken);
        var filled = await FillExpiryDatesAsync(cancellationToken);
        var (states, statesStopped) = stopped ? (0, true) : await EstimateStatesAsync(cancellationToken);
        var pendingFilled = await FillPendingAsync(cancellationToken);
        stopped |= statesStopped;
        var (classified, classifyStopped) = stopped ? (0, true) : await ClassifyAsync(cancellationToken);
        stopped |= classifyStopped;
        var (animal, animalStopped) = stopped ? (0, true) : await MarkAnimalContentAsync(cancellationToken);
        stopped |= animalStopped;
        if (estimated > 0 || filled > 0 || states > 0 || pendingFilled > 0 || classified > 0 || animal > 0 || stopped)
        {
            LogRun(estimated, low, filled, states, pendingFilled, classified, animal, stopped);
        }

        return new ShelfLifeRun(estimated, low, filled, stopped, states, pendingFilled, classified, animal);
    }

    /// <summary>Meat, fish or neither (#112), worked out once per product; a manual choice is never overwritten.</summary>
    private async Task<(int Set, bool Stopped)> MarkAnimalContentAsync(CancellationToken cancellationToken)
    {
        var pending = await products.GetNeedingAnimalContentAsync(MaxPerRun, cancellationToken);
        var set = 0;
        foreach (var product in pending)
        {
            var now = clock.GetUtcNow();
            var meatAisle = product.Aisle?.Id == Aisles.MeatFishId;
            ProductAnimalContent? answer = !meatAisle && AnimalContents.FromCategoryTags(product.Details?.CategoryTags ?? []) is { } fromTags
                ? new ProductAnimalContent(fromTags, AnimalContents.OpenFoodFactsSource, null, now)
                : null;
            if (answer is null)
            {
                switch (await estimator.ClassifyAnimalContentAsync(Describe(product), meatAisle, cancellationToken))
                {
                    case AnimalContentResult.Classified { Value: var value, Confidence: var confidence }:
                        var sure = meatAisle ? AnimalContents.IsMeatOrFish(value) : confidence >= AnimalContents.MinConfidence;
                        answer = new ProductAnimalContent(
                            sure ? value : meatAisle ? AnimalContent.Meat : AnimalContent.Unknown,
                            meatAisle ? AnimalContents.AisleSource : AnimalContents.JevSource, confidence, now);
                        break;
                    case AnimalContentResult.NotConfigured:
                        LogNotConfigured();
                        return (set, true);
                    case AnimalContentResult.Unavailable { Reason: var reason }:
                        LogUnavailable(reason, pending.Count - set);
                        return (set, true);
                    case AnimalContentResult.Failed { Reason: var reason }:
                        LogFailed(product.ProductId, reason);
                        return (set, true);
                }
            }

            if (await products.GetVersionedAsync(product.ProductId, cancellationToken) is { Value.NeedsAnimalContent: true } current
                && await products.TryReplaceAsync(current, current.Value with { Animal = answer }, cancellationToken))
            {
                set++;
                LogAnimalContent(product.ProductId, product.DisplayName, AnimalContents.Id(answer!.Value), answer.Source, answer.Confidence ?? 1);
            }
        }

        return (set, false);
    }

    /// <summary>Aisle and icon (#71, #72): asks only what's missing; a manual choice is never overwritten.</summary>
    private async Task<(int Classified, bool Stopped)> ClassifyAsync(CancellationToken cancellationToken)
    {
        var pending = await products.GetNeedingClassificationAsync(MaxPerRun, cancellationToken);
        var classified = 0;
        foreach (var product in pending)
        {
            var result = await estimator.ClassifyAsync(
                Describe(product), product.ShelfLife?.UsualStorage, product.Aisle is null, product.Icon is null, cancellationToken);
            switch (result)
            {
                case ClassificationResult.Classified { Classification: var c }:
                    if (await TrySetClassificationAsync(product.ProductId, c, cancellationToken))
                    {
                        classified++;
                        LogClassified(product.ProductId, product.DisplayName, c.AisleId, c.Icon, c.IconConfidence);
                    }

                    break;
                case ClassificationResult.NotConfigured:
                    LogNotConfigured();
                    return (classified, true);
                case ClassificationResult.Unavailable { Reason: var reason }:
                    LogUnavailable(reason, pending.Count - classified);
                    return (classified, true);
                case ClassificationResult.Failed { Reason: var reason }:
                    LogFailed(product.ProductId, reason);
                    return (classified, true);
            }
        }

        return (classified, false);
    }

    private async Task<bool> TrySetClassificationAsync(Guid productId, Classification c, CancellationToken cancellationToken)
    {
        if (await products.GetVersionedAsync(productId, cancellationToken) is not { Value.NeedsClassification: true } current)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var product = current.Value;
        var aisle = product.Aisle ?? (Aisles.Find(c.AisleId) is { } a ? new ProductAisle(a.Id, ShelfLife.JevSource, c.AisleConfidence, now) : null);
        var icon = product.Icon ?? (c.Icon is null && c.IconConfidence <= 0 ? null : new ProductIcon(
            ProductIcons.IsKnown(c.Icon) && c.IconConfidence >= ProductIcons.MinConfidence ? c.Icon : null,
            ShelfLife.JevSource, c.IconConfidence, now));
        return await products.TryReplaceAsync(current, product with { Aisle = aisle, Icon = icon }, cancellationToken);
    }

    private async Task<(int Estimated, bool Stopped)> EstimateStatesAsync(CancellationToken cancellationToken)
    {
        var pending = await products.GetNeedingStateEstimatesAsync(MaxPerRun, cancellationToken);
        var estimated = 0;
        foreach (var product in pending)
        {
            var result = await estimator.EstimateStatesAsync(Describe(product), product.ShelfLife!.UsualStorage, cancellationToken);
            switch (result)
            {
                case StateEstimatesResult.Estimated { Estimates: var e }:
                    if (await TrySetStatesAsync(product.ProductId, e, cancellationToken))
                    {
                        estimated++;
                        LogStatesEstimated(product.ProductId, product.DisplayName, e.Opened.Days, e.Opened.Storage,
                            e.Frozen.Days, e.Defrosted.Days, e.Defrosted.Storage);
                    }

                    break;
                case StateEstimatesResult.NotConfigured:
                    LogNotConfigured();
                    return (estimated, true);
                case StateEstimatesResult.Unavailable { Reason: var reason }:
                    LogUnavailable(reason, pending.Count - estimated);
                    return (estimated, true);
                case StateEstimatesResult.Failed { Reason: var reason }:
                    LogFailed(product.ProductId, reason);
                    return (estimated, true);
            }
        }

        return (estimated, false);
    }

    /// <summary>Stores Jev's state estimates, keeping a manual "once opened" (ETag-guarded; a conflict waits for the next run).</summary>
    private async Task<bool> TrySetStatesAsync(Guid productId, StateEstimates e, CancellationToken cancellationToken)
    {
        if (await products.GetVersionedAsync(productId, cancellationToken) is not { Value.NeedsStateEstimates: true } current)
        {
            return false;
        }

        static StateShelfLife Jev(StateEstimate s) => new(s.Days, s.Storage, s.Confidence, s.LowConfidence, ShelfLife.JevSource);
        var states = current.Value.States.FromJev(
            Jev(e.Opened), Jev(e.Frozen with { Storage = Storage.Freezer }), Jev(e.Defrosted), clock.GetUtcNow());
        return await products.TryReplaceAsync(current, current.Value with { StateEstimates = states }, cancellationToken);
    }

    /// <summary>Units opened, frozen or defrosted before their product had that estimate: worked out now if it has one.</summary>
    private async Task<int> FillPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await units.GetInStockEstimatePendingAsync(cancellationToken);
        var filled = 0;
        foreach (var group in pending.GroupBy(u => u.Value.ProductId))
        {
            if (await products.GetAsync(group.Key, cancellationToken) is not { } product)
            {
                continue;
            }

            foreach (var unit in group)
            {
                if (UnitStates.Estimate(unit.Value, product.States) is { } estimated
                    && await units.TryReplaceAsync(unit, estimated, cancellationToken))
                {
                    filled++;
                }
            }
        }

        return filled;
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

            // A unit opened, frozen or defrosted waits for that estimate instead (#65).
            foreach (var unit in group.Where(u => u.Value.ExpirySource is null or ExpirySource.ShelfLife && !u.Value.EstimatePending))
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
        Message = "Shelf-life run: {Estimated} products estimated ({Low} low confidence), {Filled} units given an estimated expiry date, {States} products given state estimates, {Pending} pending units filled, {Classified} products given an aisle or icon, {Animal} marked meat, fish or neither, stopped early: {Stopped}.")]
    private partial void LogRun(int estimated, int low, int filled, int states, int pending, int classified, int animal, bool stopped);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Animal content for {ProductId} ({DisplayName}): {Value} from {Source} (confidence {Confidence:0.00}).")]
    private partial void LogAnimalContent(Guid productId, string displayName, string value, string source, double confidence);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Aisle and icon for {ProductId} ({DisplayName}): aisle {Aisle}, icon {Icon} (confidence {IconConfidence:0.00}).")]
    private partial void LogClassified(Guid productId, string displayName, string? aisle, string? icon, double iconConfidence);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "State estimates for {ProductId} ({DisplayName}): opened {OpenedDays} days in the {OpenedStorage}, frozen {FrozenDays} days, defrosted {DefrostedDays} days in the {DefrostedStorage}.")]
    private partial void LogStatesEstimated(Guid productId, string displayName, int openedDays, Storage openedStorage, int frozenDays, int defrostedDays, Storage defrostedStorage);

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
