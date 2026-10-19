using CupboardTracker.Application.Products;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.GenericNaming;

/// <summary>What one run did (#90).</summary>
public sealed record GenericNameRun(int InAisle, int ByRetry, int Generated, int Reused, int Fallback, bool Stopped)
{
    public int Total => InAisle + ByRetry + Generated + Reused + Fallback;
}

/// <summary>
/// Gives known products a generic name (#90), one product at a time so names created earlier are matched by later
/// ones, after the aisle is known. For each product:
/// <list type="number">
/// <item><b>Match in the aisle</b> (Jev): the aisle's names plus "None of these", in batches of
/// <see cref="GenericNameSettings.BatchSize"/> asked in parallel, with a run-off when more than one batch matches.</item>
/// <item><b>Word-matching retry</b>: names in other aisles sharing a meaningful word, asked the same way (catches a
/// product filed in the wrong aisle).</item>
/// <item><b>Generate</b> (Foundry): only when neither finds a name. Code decides reuse: a name that normalises to an
/// existing record uses it.</item>
/// </list>
/// A manual name is never overwritten. If Jev or Foundry can't be reached, the run stops and the timer tries again.
/// </summary>
public sealed partial class AssignGenericNames(
    IProductRepository products,
    IGenericNameRepository names,
    IGenericNameMatcher matcher,
    IGenericNameWriter writer,
    GenericNameSettings settings,
    TimeProvider clock,
    ILogger<AssignGenericNames> logger)
{
    public const int MaxPerRun = 40;
    public const int MaxExamples = 40;

    private enum Outcome { InAisle, ByRetry, Generated, Reused, Fallback, Skipped, Stop }

    public async Task<GenericNameRun> ExecuteAsync(CancellationToken cancellationToken)
    {
        var pending = await products.GetNeedingGenericNameAsync(MaxPerRun, cancellationToken);
        var counts = new Dictionary<Outcome, int>();
        var stopped = false;
        foreach (var product in pending)
        {
            var outcome = await AssignAsync(product, cancellationToken);
            if (outcome == Outcome.Stop)
            {
                stopped = true;
                break;
            }

            counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
        }

        var run = new GenericNameRun(counts.GetValueOrDefault(Outcome.InAisle), counts.GetValueOrDefault(Outcome.ByRetry),
            counts.GetValueOrDefault(Outcome.Generated), counts.GetValueOrDefault(Outcome.Reused),
            counts.GetValueOrDefault(Outcome.Fallback), stopped);
        if (run.Total > 0 || stopped)
        {
            LogRun(run.InAisle, run.ByRetry, run.Generated, run.Reused, run.Fallback, stopped);
        }

        return run;
    }

    private async Task<Outcome> AssignAsync(Product product, CancellationToken cancellationToken)
    {
        if (product.Aisle is not { } productAisle)
        {
            return Outcome.Skipped;
        }

        var description = EstimateShelfLives.Describe(product);
        var all = await names.ListAsync(cancellationToken);
        var inAisle = all.Where(n => n.AisleId == productAisle.Id).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();

        // 1. Match in the aisle.
        if (inAisle.Count > 0)
        {
            var (match, stop) = await MatchBatchedAsync(product, description, inAisle, cancellationToken);
            if (stop)
            {
                return Outcome.Stop;
            }

            if (match is { } m)
            {
                return await SetAsync(product, m.Name, GenericNameSource.JevMatch, m.Confidence, Outcome.InAisle, cancellationToken);
            }
        }

        // 2. Word-matching retry across the other aisles.
        var words = GenericNames.Words(product.DisplayName, product.Details?.Brand);
        var shortlist = all.Where(n => n.AisleId != productAisle.Id && GenericNames.Words(n.Name).Overlaps(words))
            .OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        if (shortlist.Count > 0)
        {
            var (match, stop) = await MatchBatchedAsync(product, description, shortlist, cancellationToken);
            if (stop)
            {
                return Outcome.Stop;
            }

            if (match is { } m)
            {
                return await SetAsync(product, m.Name, GenericNameSource.JevMatch, m.Confidence, Outcome.ByRetry, cancellationToken);
            }
        }

        // 3. Generate a new name, with the aisle's names as examples of the style.
        var aisleName = Aisles.Of(productAisle.Id).Name;
        var examples = inAisle.Select(n => n.Name).Take(MaxExamples).ToList();
        switch (await writer.WriteAsync(description, aisleName, examples, cancellationToken))
        {
            case GenericWriteResult.Written { Name: var written }:
                var source = GenericNameSource.Foundry;
                if (GenericNames.Problem(written, product.Details?.Brand) is { } problem)
                {
                    LogInvalidGenerated(product.ProductId, problem);
                    written = GenericNames.FallbackFrom(product.DisplayName);
                    source = GenericNameSource.Fallback;
                }

                var existing = await names.GetAsync(GenericNames.IdOf(written), cancellationToken);
                var record = existing ?? await names.GetOrCreateAsync(
                    GenericName.Create(written, productAisle.Id, clock.GetUtcNow(), GenericName.FoundrySource), cancellationToken);
                var outcome = source == GenericNameSource.Fallback ? Outcome.Fallback : existing is null ? Outcome.Generated : Outcome.Reused;
                return await SetAsync(product, record, source, null, outcome, cancellationToken);
            case GenericWriteResult.NotConfigured:
                LogNotConfigured();
                return Outcome.Stop;
            case GenericWriteResult.Unavailable { Reason: var reason }:
                LogUnavailable("Foundry", reason);
                return Outcome.Stop;
            case GenericWriteResult.Failed { Reason: var reason }:
                LogFailed("Foundry", product.ProductId, reason);
                return Outcome.Stop;
            default:
                return Outcome.Stop;
        }
    }

    /// <summary>Asks Jev over the options in batches, in parallel; a run-off decides between batch winners. Never
    /// compares confidences across batches.</summary>
    private async Task<(GenericMatchResult.Matched? Match, bool Stop)> MatchBatchedAsync(
        Product product, ProductDescription description, IReadOnlyList<GenericName> options, CancellationToken cancellationToken)
    {
        var batches = options.Chunk(Math.Max(1, settings.BatchSize)).ToList();
        var results = await Task.WhenAll(batches.Select(b => matcher.MatchAsync(description, b, cancellationToken)));
        if (Problem(results, product.ProductId))
        {
            return (null, true);
        }

        var winners = results.OfType<GenericMatchResult.Matched>().Where(m => m.Confidence >= settings.MatchMinConfidence).ToList();
        if (winners.Count <= 1)
        {
            return (winners.SingleOrDefault(), false);
        }

        var runOff = await matcher.MatchAsync(description, [.. winners.Select(w => w.Name)], cancellationToken);
        if (Problem([runOff], product.ProductId))
        {
            return (null, true);
        }

        return (runOff is GenericMatchResult.Matched m && m.Confidence >= settings.MatchMinConfidence ? m : null, false);
    }

    private bool Problem(IEnumerable<GenericMatchResult> results, Guid productId)
    {
        foreach (var result in results)
        {
            switch (result)
            {
                case GenericMatchResult.NotConfigured:
                    LogNotConfigured();
                    return true;
                case GenericMatchResult.Unavailable { Reason: var reason }:
                    LogUnavailable("Jev", reason);
                    return true;
                case GenericMatchResult.Failed { Reason: var reason }:
                    LogFailed("Jev", productId, reason);
                    return true;
            }
        }

        return false;
    }

    /// <summary>Stores the name if the product still needs one (ETag-guarded; a manual choice made meanwhile wins).</summary>
    private async Task<Outcome> SetAsync(
        Product product, GenericName name, GenericNameSource source, double? confidence, Outcome outcome, CancellationToken cancellationToken)
    {
        if (await products.GetVersionedAsync(product.ProductId, cancellationToken) is not { Value.NeedsGenericName: true } current)
        {
            return Outcome.Skipped;
        }

        var generic = new ProductGenericName(name.Id, name.Name, source, confidence, clock.GetUtcNow());
        if (!await products.TryReplaceAsync(current, current.Value with { Generic = generic }, cancellationToken))
        {
            return Outcome.Skipped;
        }

        LogAssigned(product.ProductId, name.Id, outcome.ToString(), confidence);
        return outcome;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Generic name for {ProductId}: {GenericNameId} ({Outcome}, confidence {Confidence}).")]
    private partial void LogAssigned(Guid productId, string genericNameId, string outcome, double? confidence);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Generic names run: {InAisle} matched in the aisle, {ByRetry} by the word retry, {Generated} generated, {Reused} generated but reused, {Fallback} fell back, stopped early: {Stopped}.")]
    private partial void LogRun(int inAisle, int byRetry, int generated, int reused, int fallback, bool stopped);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A generated generic name for {ProductId} was unusable ({Problem}); using its display name without sizes.")]
    private partial void LogInvalidGenerated(Guid productId, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Generic names aren't configured (no Jev key or Foundry endpoint yet): products wait.")]
    private partial void LogNotConfigured();

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service} couldn't be reached for generic names ({Reason}); the next run tries again.")]
    private partial void LogUnavailable(string service, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Service} refused the generic name request for {ProductId} ({Reason}); it will be retried, but this needs a fix.")]
    private partial void LogFailed(string service, Guid productId, string reason);
}
