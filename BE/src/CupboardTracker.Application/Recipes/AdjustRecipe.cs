using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Recipes;

/// <summary>What the household asked to change (#104), as sent by the app: the recipe as it was last shown, products to
/// add, things to add that aren't in stock, ingredient names to remove, and a tweak.</summary>
public sealed record AdjustChoices(
    Recipe Current, IReadOnlyList<Guid> AddProductIds, IReadOnlyList<string> AddTyped, IReadOnlyList<string> Remove, string? Tweak);

/// <summary>What changed, for the line under the meta: <c>Adjusted: + Chorizo · − Onion · "spicier"</c>.</summary>
public sealed record AdjustmentSummary(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, string? Tweak);

public abstract record AdjustRecipeResult
{
    public sealed record Adjusted(RecipeView Recipe, AdjustmentSummary Summary) : AdjustRecipeResult;

    public sealed record Invalid(string Error) : AdjustRecipeResult;

    public sealed record NothingInStock : AdjustRecipeResult;

    public sealed record TooManyRequests : AdjustRecipeResult;

    public sealed record Unavailable : AdjustRecipeResult;
}

/// <summary>
/// <c>POST /api/recipes/adjust</c> (#104): the same recipe, updated with the household's changes. The stock is rebuilt
/// from Cosmos, as for a suggestion; the recipe from the app is only the starting point. Counts towards the same
/// hourly limit as suggestions. Stateless, and only metadata is logged (never the recipe or the tweak).
/// </summary>
public sealed partial class AdjustRecipe(GetStock stock, IRecipeGenerator generator, RecipeRateLimit rateLimit, ILogger<AdjustRecipe> logger)
{
    public const int MaxTweakLength = 300;
    public const int MaxTyped = 3;
    public const int MaxTypedLength = 40;
    public const int MaxAdded = 10;

    public async Task<AdjustRecipeResult> ExecuteAsync(AdjustChoices choices, CurrentUser user, CancellationToken cancellationToken)
    {
        var tweak = string.IsNullOrWhiteSpace(choices.Tweak) ? null : choices.Tweak.Trim();
        if (tweak?.Length > MaxTweakLength)
        {
            return new AdjustRecipeResult.Invalid($"The tweak can be at most {MaxTweakLength} characters.");
        }

        var typed = choices.AddTyped.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (typed.Count > MaxTyped || typed.Any(t => t.Length > MaxTypedLength))
        {
            return new AdjustRecipeResult.Invalid($"Add at most {MaxTyped} things that aren't in stock, each up to {MaxTypedLength} characters.");
        }

        var add = choices.AddProductIds.Distinct().ToList();
        if (add.Count > MaxAdded)
        {
            return new AdjustRecipeResult.Invalid($"Add at most {MaxAdded} ingredients at a time.");
        }

        // Only names that are in the recipe can be removed; anything else is ignored.
        var remove = choices.Current.Ingredients
            .Where(i => choices.Remove.Any(r => string.Equals(r.Trim(), i.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var changes = new RecipeChanges(add, typed, remove, tweak);
        if (changes.IsEmpty)
        {
            return new AdjustRecipeResult.Invalid("Choose something to add, remove or tweak.");
        }

        var view = await stock.ExecuteAsync(cancellationToken);
        var known = view.Aisles.SelectMany(a => a.Products.Select(p => (Aisle: a, Product: p))).ToList();
        if (known.Count == 0)
        {
            return new AdjustRecipeResult.NothingInStock();
        }

        var byId = known.ToDictionary(k => k.Product.ProductId, k => k.Product);
        if (add.FirstOrDefault(id => !byId.ContainsKey(id)) is var missing && missing != Guid.Empty)
        {
            return new AdjustRecipeResult.Invalid($"Product {missing} isn't in stock.");
        }

        if (!rateLimit.TryTake(user.ObjectId))
        {
            LogRateLimited();
            return new AdjustRecipeResult.TooManyRequests();
        }

        var context = new RecipeRequest(
            choices.Current.Meal,
            false,
            [.. known.Select(k => new RecipeStockItem(k.Product.ProductId, k.Product.DisplayName, k.Product.Count,
                k.Product.DaysUntilSoonest, k.Product.UseFirst, k.Aisle.Name, k.Product.OpenedCount))],
            add,
            tweak,
            []);
        var result = await generator.AdjustAsync(new RecipeAdjustmentRequest(context, choices.Current, changes), cancellationToken);
        if (result is not RecipeResult.Suggested { Recipe: var recipe })
        {
            LogUnavailable(add.Count, typed.Count, remove.Count, tweak is not null, ((RecipeResult.Unavailable)result).Reason);
            return new AdjustRecipeResult.Unavailable();
        }

        var shown = SuggestRecipe.ToView(recipe, byId);
        var couldNotAdd = shown.CouldNotFitIn.Concat(recipe.CouldNotAddTyped ?? []).ToList();
        var couldNotRemove = recipe.CouldNotRemove ?? [];
        shown = shown with { CouldNotFitIn = [], CouldNotAdd = couldNotAdd, CouldNotRemove = couldNotRemove };
        var summary = new AdjustmentSummary(
            [.. add.Select(id => byId[id].GenericName ?? byId[id].DisplayName).Concat(typed)],
            [.. remove.Select(r => r.Name)],
            tweak);
        LogAdjusted(add.Count, typed.Count, remove.Count, tweak is not null, couldNotAdd.Count, couldNotRemove.Count);
        return new AdjustRecipeResult.Adjusted(shown, summary);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Recipe adjusted: {Added} added from stock, {Typed} typed, {Removed} removed, tweak given {TweakGiven}; couldn't add {NotAdded}, couldn't remove {NotRemoved}.")]
    private partial void LogAdjusted(int added, int typed, int removed, bool tweakGiven, int notAdded, int notRemoved);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Recipe adjustment unavailable: {Added} added, {Typed} typed, {Removed} removed, tweak given {TweakGiven} ({Reason}); the app says try again.")]
    private partial void LogUnavailable(int added, int typed, int removed, bool tweakGiven, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recipe adjustment refused: over the hourly limit.")]
    private partial void LogRateLimited();
}
