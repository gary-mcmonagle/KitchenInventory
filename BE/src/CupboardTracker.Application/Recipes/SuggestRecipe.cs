using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Recipes;

/// <summary>What the household chose on the Hungry? form (#73, #88).</summary>
public sealed record RecipeChoices(
    RecipeMeal Meal, bool PreferUseFirst, IReadOnlyList<Guid> IncludeProductIds, string? Comments, IReadOnlyList<string> AvoidTitles,
    RecipeMode Mode = RecipeMode.Cook);

/// <summary>An ingredient as the app shows it: in-stock ones carry the product's current name and use-first flag.</summary>
public sealed record RecipeIngredientView(string Name, string Amount, Guid? ProductId, bool UseFirst);

/// <summary>An in-stock product the recipe uses up, with what its badge needs.</summary>
public sealed record RecipeProductView(Guid ProductId, string DisplayName, int? DaysUntilSoonest, DateOnly? SoonestEstimatedExpiryDate);

/// <summary>A recipe as the app shows it. After an adjustment (#104), <see cref="CouldNotAdd"/> and
/// <see cref="CouldNotRemove"/> say what the model couldn't do ("Couldn't add: …").</summary>
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
    IReadOnlyList<string> CouldNotFitIn,
    IReadOnlyList<string>? CouldNotAdd = null,
    IReadOnlyList<string>? CouldNotRemove = null);

/// <summary>An in-stock product a Ready now idea uses, with what its link and dot need.</summary>
public sealed record QuickIdeaProductView(
    Guid ProductId, string DisplayName, bool UseFirst, int? DaysUntilSoonest, DateOnly? SoonestEstimatedExpiryDate);

public sealed record QuickIdeaView(
    string Title, string How, Appliance Appliance, int Minutes, IReadOnlyList<QuickIdeaProductView> Uses, string? AlsoNeeded);

public abstract record SuggestRecipeResult
{
    public sealed record Suggested(RecipeView Recipe) : SuggestRecipeResult;

    /// <summary>Ready now (#88): ideas using use-first items come first when "prefer use first" is on.</summary>
    public sealed record IdeasSuggested(RecipeMeal Meal, IReadOnlyList<QuickIdeaView> Ideas) : SuggestRecipeResult;

    public sealed record Invalid(string Error) : SuggestRecipeResult;

    public sealed record NothingInStock : SuggestRecipeResult;

    public sealed record TooManyRequests : SuggestRecipeResult;

    public sealed record Unavailable : SuggestRecipeResult;
}

/// <summary>
/// <c>POST /api/recipes/suggest</c> (#73, #88): a recipe, or Ready now ideas, from what's in stock. The stock comes from Cosmos (known products
/// only), never from the app. Stateless: nothing is stored, and only metadata is logged (never the comments).
/// </summary>
public sealed partial class SuggestRecipe(
    GetStock stock, IRecipeGenerator generator, RecipeRateLimit rateLimit, ILogger<SuggestRecipe> logger)
{
    public const int MaxCommentsLength = 300;
    public const int MaxIncluded = 20;
    public const int MaxAvoidTitles = 20;
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

        if (choices.Mode == RecipeMode.ReadyNow)
        {
            var ideas = await generator.SuggestQuickIdeasAsync(request, cancellationToken);
            if (ideas is not QuickIdeasResult.Suggested { Ideas: var list })
            {
                LogUnavailable(choices.Mode, choices.Meal, comments is not null, ((QuickIdeasResult.Unavailable)ideas).Reason);
                return new SuggestRecipeResult.Unavailable();
            }

            var views = list.Select(i => ToView(i, byId)).ToList();
            if (choices.PreferUseFirst)
            {
                views = [.. views.OrderBy(v => v.Uses.Any(u => u.UseFirst) ? 0 : 1)]; // stable: the model's order otherwise
            }

            LogIdeas(choices.Meal, comments is not null, include.Count, views.Count);
            return new SuggestRecipeResult.IdeasSuggested(choices.Meal, views);
        }

        var result = await generator.SuggestAsync(request, cancellationToken);
        if (result is not RecipeResult.Suggested { Recipe: var recipe })
        {
            LogUnavailable(choices.Mode, choices.Meal, comments is not null, ((RecipeResult.Unavailable)result).Reason);
            return new SuggestRecipeResult.Unavailable();
        }

        LogSuggested(choices.Meal, comments is not null, include.Count, recipe.CouldNotFitIn.Count);
        return new SuggestRecipeResult.Suggested(ToView(recipe, byId));
    }

    internal static QuickIdeaView ToView(QuickIdea idea, Dictionary<Guid, StockProduct> byId) => new(
        idea.Title,
        idea.How,
        idea.Appliance,
        idea.Minutes,
        [.. idea.Uses.Where(byId.ContainsKey).Select(id => byId[id])
            .Select(p => new QuickIdeaProductView(p.ProductId, p.DisplayName, p.UseFirst, p.DaysUntilSoonest, p.SoonestEstimatedExpiryDate))],
        idea.AlsoNeeded);

    internal static RecipeView ToView(Recipe recipe, Dictionary<Guid, StockProduct> byId) => new(
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

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Ready now ideas suggested: meal {Meal}, comments given {CommentsGiven}, {Included} included, {Ideas} ideas.")]
    private partial void LogIdeas(RecipeMeal meal, bool commentsGiven, int included, int ideas);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Recipe unavailable: mode {Mode}, meal {Meal}, comments given {CommentsGiven} ({Reason}); the app says try again.")]
    private partial void LogUnavailable(RecipeMode mode, RecipeMeal meal, bool commentsGiven, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recipe request refused: over the hourly limit.")]
    private partial void LogRateLimited();
}
