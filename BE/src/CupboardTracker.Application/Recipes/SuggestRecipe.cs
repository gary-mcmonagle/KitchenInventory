using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Recipes;

/// <summary>What the household chose on the Recipes form (#73).</summary>
public sealed record RecipeChoices(
    RecipeMeal Meal, bool PreferUseFirst, IReadOnlyList<Guid> IncludeProductIds, string? Comments, IReadOnlyList<string> AvoidTitles);

/// <summary>An ingredient as the app shows it: in-stock ones carry the product's current name and use-first flag.</summary>
public sealed record RecipeIngredientView(string Name, string Amount, Guid? ProductId, bool UseFirst);

/// <summary>An in-stock product the recipe uses up, with what its badge needs.</summary>
public sealed record RecipeProductView(Guid ProductId, string DisplayName, int? DaysUntilSoonest, DateOnly? SoonestEstimatedExpiryDate);

public sealed record RecipeView(
    string Title,
    string Summary,
    RecipeMeal Meal,
    int Servings,
    int TotalMinutes,
    IReadOnlyList<RecipeIngredientView> Ingredients,
    IReadOnlyList<string> Steps,
    IReadOnlyList<RecipeProductView> UsesUp,
    IReadOnlyList<string> AlsoNeeded,
    IReadOnlyList<string> CouldNotFitIn);

public abstract record SuggestRecipeResult
{
    public sealed record Suggested(RecipeView Recipe) : SuggestRecipeResult;

    public sealed record Invalid(string Error) : SuggestRecipeResult;

    public sealed record NothingInStock : SuggestRecipeResult;

    public sealed record TooManyRequests : SuggestRecipeResult;

    public sealed record Unavailable : SuggestRecipeResult;
}

/// <summary>
/// <c>POST /api/recipes/suggest</c> (#73): a recipe from what's in stock. The stock comes from Cosmos (known products
/// only), never from the app. Stateless: nothing is stored, and only metadata is logged (never the comments).
/// </summary>
public sealed partial class SuggestRecipe(
    GetStock stock, IRecipeGenerator generator, RecipeRateLimit rateLimit, ILogger<SuggestRecipe> logger)
{
    public const int MaxCommentsLength = 300;
    public const int MaxIncluded = 20;
    public const int MaxAvoidTitles = 10;
    public const int MaxTitleLength = 80;

    public async Task<SuggestRecipeResult> ExecuteAsync(RecipeChoices choices, CurrentUser user, CancellationToken cancellationToken)
    {
        var comments = string.IsNullOrWhiteSpace(choices.Comments) ? null : choices.Comments.Trim();
        if (comments?.Length > MaxCommentsLength)
        {
            return new SuggestRecipeResult.Invalid($"Comments can be at most {MaxCommentsLength} characters.");
        }

        if (choices.IncludeProductIds.Count > MaxIncluded)
        {
            return new SuggestRecipeResult.Invalid($"Pick at most {MaxIncluded} ingredients.");
        }

        var view = await stock.ExecuteAsync(cancellationToken);
        var known = view.Aisles.SelectMany(a => a.Products.Select(p => (Aisle: a, Product: p))).ToList();
        if (known.Count == 0)
        {
            return new SuggestRecipeResult.NothingInStock();
        }

        var byId = known.ToDictionary(k => k.Product.ProductId, k => k.Product);
        var include = choices.IncludeProductIds.Distinct().ToList();
        if (include.FirstOrDefault(id => !byId.ContainsKey(id)) is var missing && missing != Guid.Empty)
        {
            return new SuggestRecipeResult.Invalid($"Product {missing} isn't in stock.");
        }

        if (!rateLimit.TryTake(user.ObjectId))
        {
            LogRateLimited();
            return new SuggestRecipeResult.TooManyRequests();
        }

        var request = new RecipeRequest(
            choices.Meal,
            choices.PreferUseFirst,
            [.. known.Select(k => new RecipeStockItem(k.Product.ProductId, k.Product.DisplayName, k.Product.Count,
                k.Product.DaysUntilSoonest, k.Product.UseFirst, k.Aisle.Name, k.Product.OpenedCount))],
            include,
            comments,
            [.. choices.AvoidTitles.Select(t => t.Trim()).Where(t => t.Length > 0)
                .Select(t => t.Length > MaxTitleLength ? t[..MaxTitleLength] : t).TakeLast(MaxAvoidTitles)]);

        var result = await generator.SuggestAsync(request, cancellationToken);
        if (result is not RecipeResult.Suggested { Recipe: var recipe })
        {
            LogUnavailable(choices.Meal, comments is not null, ((RecipeResult.Unavailable)result).Reason);
            return new SuggestRecipeResult.Unavailable();
        }

        LogSuggested(choices.Meal, comments is not null, include.Count, recipe.CouldNotFitIn.Count);
        return new SuggestRecipeResult.Suggested(ToView(recipe, byId));
    }

    private static RecipeView ToView(Recipe recipe, Dictionary<Guid, StockProduct> byId) => new(
        recipe.Title,
        recipe.Summary,
        recipe.Meal,
        recipe.Servings,
        recipe.TotalMinutes,
        [.. recipe.Ingredients.Select(i => i.ProductId is { } id && byId.TryGetValue(id, out var p)
            ? new RecipeIngredientView(p.DisplayName, i.Amount, id, p.UseFirst)
            : new RecipeIngredientView(i.Name, i.Amount, null, false))],
        recipe.Steps,
        [.. recipe.UsesUp.Where(byId.ContainsKey).Select(id => byId[id])
            .Select(p => new RecipeProductView(p.ProductId, p.DisplayName, p.DaysUntilSoonest, p.SoonestEstimatedExpiryDate))],
        recipe.AlsoNeeded,
        [.. recipe.CouldNotFitIn.Where(byId.ContainsKey).Select(id => byId[id].DisplayName)]);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Recipe suggested: meal {Meal}, comments given {CommentsGiven}, {Included} included, {NotFitted} couldn't be fitted in.")]
    private partial void LogSuggested(RecipeMeal meal, bool commentsGiven, int included, int notFitted);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Recipe unavailable: meal {Meal}, comments given {CommentsGiven} ({Reason}); the app says try again.")]
    private partial void LogUnavailable(RecipeMeal meal, bool commentsGiven, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recipe request refused: over the hourly limit.")]
    private partial void LogRateLimited();
}
