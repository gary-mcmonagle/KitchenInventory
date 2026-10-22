using System.Diagnostics;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Recipes;

/// <summary>What Jev is told about a recipe when picking additions (#104): its title, meal, ingredient names and a
/// one-line idea of the method.</summary>
public sealed record RecipeSketch(string Title, RecipeMeal Meal, IReadOnlyList<string> Ingredients, string Method);

public abstract record AdditionRanking
{
    /// <summary>How likely each option is to improve the recipe most (same order as asked), and "Nothing would
    /// improve it".</summary>
    public sealed record Ranked(IReadOnlyList<double> Probabilities, double Nothing) : AdditionRanking;

    public sealed record Unavailable(string Reason) : AdditionRanking;
}

/// <summary>Asks which of some in-stock names would improve a recipe most as an addition (Jev <c>choice</c> in
/// Infrastructure). Callers keep the options within Jev's limit (<see cref="RecipeAdditionSettings.BatchSize"/>).</summary>
public interface IRecipeAdditionRanker
{
    Task<AdditionRanking> RankAsync(RecipeSketch recipe, IReadOnlyList<string> options, CancellationToken cancellationToken);
}

/// <summary>From app settings (#104): RECIPE_ADDITION_MIN_CONFIDENCE (default 0.1: an option Jev gives less than this
/// isn't suggested). The batch size is Jev's 255 options less "Nothing would improve it".</summary>
public sealed record RecipeAdditionSettings(
    double MinConfidence = RecipeAdditionSettings.DefaultMinConfidence, int BatchSize = RecipeAdditionSettings.DefaultBatchSize)
{
    public const double DefaultMinConfidence = 0.1;
    public const int DefaultBatchSize = 254;
}

/// <summary>A suggested addition: a generic name (or display name) in stock, its products, and whether any of them is
/// use first.</summary>
public sealed record RecipeAddition(string Name, IReadOnlyList<Guid> ProductIds, bool UseFirst, double Confidence);

/// <summary>
/// <c>POST /api/recipes/suggest-additions</c> (#104): up to 3 in-stock things that would improve a recipe, for the
/// Adjust sheet's chips. Candidates are every in-stock known product grouped by generic name (display name when it has
/// none), leaving out what the recipe already has. Jev ranks them in one <c>choice</c> question (probabilities for
/// every option), or, with more than <see cref="RecipeAdditionSettings.BatchSize"/>, in batches whose top picks meet
/// in a run-off. Up to 3 that reach <see cref="RecipeAdditionSettings.MinConfidence"/> and beat "Nothing would improve
/// it"; among those within 0.1 of each other, use-first ones come first. No Foundry call, nothing stored, no rate
/// limit (it's cheap); only metadata is logged.
/// </summary>
public sealed partial class SuggestRecipeAdditions(
    GetStock stock, IRecipeAdditionRanker ranker, RecipeAdditionSettings settings, ILogger<SuggestRecipeAdditions> logger)
{
    public const int MaxSuggestions = 3;
    public const double NudgeWindow = 0.1;

    public async Task<IReadOnlyList<RecipeAddition>> ExecuteAsync(Recipe recipe, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var view = await stock.ExecuteAsync(cancellationToken);
        var candidates = Candidates(view.Aisles.SelectMany(a => a.Products), recipe);
        if (candidates.Count == 0)
        {
            return [];
        }

        var sketch = Sketch(recipe);
        var batches = candidates.Chunk(Math.Max(1, settings.BatchSize)).ToList();
        var picks = new List<(Candidate Candidate, double P)>();
        foreach (var batch in batches)
        {
            var ranking = await ranker.RankAsync(sketch, [.. batch.Select(c => c.Name)], cancellationToken);
            if (ranking is not AdditionRanking.Ranked ranked)
            {
                LogUnavailable(candidates.Count, batches.Count, ((AdditionRanking.Unavailable)ranking).Reason);
                return [];
            }

            picks.AddRange(Top(batch, ranked));
        }

        if (batches.Count > 1 && picks.Count > 0)
        {
            // A run-off between each batch's winners: probabilities from different questions can't be compared.
            var finalists = picks.Select(p => p.Candidate).ToList();
            if (await ranker.RankAsync(sketch, [.. finalists.Select(c => c.Name)], cancellationToken) is not AdditionRanking.Ranked runOff)
            {
                LogUnavailable(candidates.Count, batches.Count, "run-off unavailable");
                return [];
            }

            picks = Top(finalists, runOff);
        }

        var suggestions = Nudge([.. picks.Take(MaxSuggestions)])
            .Select(p => new RecipeAddition(p.Candidate.Name, p.Candidate.ProductIds, p.Candidate.UseFirst, Math.Round(p.P, 3)))
            .ToList();
        LogSuggested(candidates.Count, batches.Count, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, suggestions.Count);
        return suggestions;
    }

    internal sealed record Candidate(string Name, IReadOnlyList<Guid> ProductIds, bool UseFirst);

    /// <summary>In-stock products grouped by generic name (display name when there's none), less anything the recipe
    /// already has: the same product, or a name that one of its ingredients contains (or is contained in).</summary>
    internal static List<Candidate> Candidates(IEnumerable<StockProduct> products, Recipe recipe)
    {
        var inRecipe = recipe.Ingredients.Where(i => i.ProductId is not null).Select(i => i.ProductId!.Value).ToHashSet();
        var names = recipe.Ingredients.Select(i => GenericNames.IdOf(i.Name)).Where(n => n.Length > 0).ToList();
        bool InRecipe(string name)
        {
            var key = GenericNames.IdOf(name);
            return names.Any(n => n == key || (key.Length >= 3 && n.Contains(key, StringComparison.Ordinal))
                || (n.Length >= 3 && key.Contains(n, StringComparison.Ordinal)));
        }

        return
        [
            .. products
                .GroupBy(p => GenericNames.IdOf(p.GenericName ?? p.DisplayName))
                .Where(g => g.Key.Length > 0)
                .Select(g => new Candidate(g.First().GenericName ?? g.First().DisplayName, [.. g.Select(p => p.ProductId)], g.Any(p => p.UseFirst)))
                .Where(c => !c.ProductIds.Any(inRecipe.Contains) && !InRecipe(c.Name))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    internal static RecipeSketch Sketch(Recipe recipe)
    {
        var method = string.Join(" ", recipe.Steps.Select(s => s.Text)).ReplaceLineEndings(" ");
        if (method.Length > 300)
        {
            method = method[..300].TrimEnd() + "…";
        }

        return new RecipeSketch(recipe.Title, recipe.Meal, [.. recipe.Ingredients.Select(i => i.Name)], method);
    }

    /// <summary>The options Jev rated at least the threshold and above "Nothing would improve it", best first.</summary>
    private List<(Candidate Candidate, double P)> Top(IReadOnlyList<Candidate> options, AdditionRanking.Ranked ranked) =>
    [
        .. options.Select((c, i) => (Candidate: c, P: i < ranked.Probabilities.Count ? ranked.Probabilities[i] : 0))
            .Where(x => x.P >= settings.MinConfidence && x.P > ranked.Nothing)
            .OrderByDescending(x => x.P)
            .Take(MaxSuggestions),
    ];

    /// <summary>Use-first suggestions move ahead of ones within <see cref="NudgeWindow"/> of them; Jev judges fit,
    /// the app adds the nudge.</summary>
    internal static List<(Candidate Candidate, double P)> Nudge(List<(Candidate Candidate, double P)> picks)
    {
        for (var pass = 0; pass < picks.Count; pass++)
        {
            for (var i = 0; i + 1 < picks.Count; i++)
            {
                var (a, b) = (picks[i], picks[i + 1]);
                if (!a.Candidate.UseFirst && b.Candidate.UseFirst && a.P - b.P <= NudgeWindow)
                {
                    (picks[i], picks[i + 1]) = (b, a);
                }
            }
        }

        return picks;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Recipe additions: {Candidates} candidates in {Batches} batches, {Ms} ms, {Suggested} suggested.")]
    private partial void LogSuggested(int candidates, int batches, int ms, int suggested);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Recipe additions unavailable: {Candidates} candidates in {Batches} batches ({Reason}); the sheet hides the suggestions.")]
    private partial void LogUnavailable(int candidates, int batches, string reason);
}
