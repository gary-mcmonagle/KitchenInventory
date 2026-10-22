using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Recipes;

/// <summary>One product a Ready now idea uses, as the app showed it: the name is only a fallback for one that's since
/// left the stock.</summary>
public sealed record IdeaUse(Guid ProductId, string? Name);

/// <summary>A Ready now idea as the app last showed it (#108): only the starting point for an adjustment.</summary>
public sealed record IdeaInput(string Title, string How, Appliance Appliance, int Minutes, IReadOnlyList<IdeaUse> Uses, string? AlsoNeeded);

/// <summary>What the household asked to change in a Ready now idea (#108): as <see cref="AdjustChoices"/>, for an idea.
/// <see cref="Remove"/> names things the idea uses.</summary>
public sealed record IdeaAdjustChoices(
    IdeaInput Current, RecipeMeal Meal, IReadOnlyList<Guid> AddProductIds, IReadOnlyList<string> AddTyped, IReadOnlyList<string> Remove,
    string? Tweak, Diet Diet = Diet.Any);

/// <summary>An idea adjustment for the model: the stock and meal as for ideas (<see cref="RecipeRequest.IncludeProductIds"/>
/// are the products to add, <see cref="RecipeRequest.Comments"/> the tweak), the idea as it was, and the changes.</summary>
public sealed record IdeaAdjustmentRequest(RecipeRequest Context, QuickIdea Current, RecipeChanges Changes);

public abstract record IdeaResult
{
    /// <summary>The updated idea, with what the model still got wrong after one retry.</summary>
    public sealed record Adjusted(QuickIdea Idea, IReadOnlyList<Guid> CouldNotFitIn, IReadOnlyList<string> CouldNotAddTyped,
        IReadOnlyList<string> CouldNotRemove, IReadOnlyList<string>? Warnings = null) : IdeaResult;

    public sealed record Unavailable(string Reason) : IdeaResult;
}

public abstract record AdjustIdeaResult
{
    public sealed record Adjusted(QuickIdeaView Idea, AdjustmentSummary Summary, IReadOnlyList<string> CouldNotAdd,
        IReadOnlyList<string> CouldNotRemove, IReadOnlyList<string>? Warnings = null) : AdjustIdeaResult;

    public sealed record Invalid(string Error) : AdjustIdeaResult;

    public sealed record NothingInStock : AdjustIdeaResult;

    public sealed record TooManyRequests : AdjustIdeaResult;

    public sealed record Unavailable : AdjustIdeaResult;
}

/// <summary>
/// <c>POST /api/recipes/adjust-idea</c> (#108): the same Ready now idea, updated with the household's changes and still
/// a quick idea (toaster, microwave, kettle or no cooking; at most one extra item). As <see cref="AdjustRecipe"/>: the
/// stock is rebuilt from Cosmos, it counts towards the same hourly limit, and only metadata is logged.
/// </summary>
public sealed partial class AdjustIdea(GetStock stock, IRecipeGenerator generator, RecipeRateLimit rateLimit, ILogger<AdjustIdea> logger)
{
    /// <summary>An idea has room for one thing that isn't in stock.</summary>
    public const int MaxTyped = 1;

    public async Task<AdjustIdeaResult> ExecuteAsync(IdeaAdjustChoices choices, CurrentUser user, CancellationToken cancellationToken)
    {
        var tweak = string.IsNullOrWhiteSpace(choices.Tweak) ? null : choices.Tweak.Trim();
        if (tweak?.Length > AdjustRecipe.MaxTweakLength)
        {
            return new AdjustIdeaResult.Invalid($"The tweak can be at most {AdjustRecipe.MaxTweakLength} characters.");
        }

        var typed = choices.AddTyped.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (typed.Count > MaxTyped || typed.Any(t => t.Length > AdjustRecipe.MaxTypedLength))
        {
            return new AdjustIdeaResult.Invalid($"Add at most {MaxTyped} thing that isn't in stock, up to {AdjustRecipe.MaxTypedLength} characters.");
        }

        if (choices.Diet == Diet.Half)
        {
            return new AdjustIdeaResult.Invalid("Half & half is for Cook something only.");
        }

        var add = choices.AddProductIds.Distinct().ToList();
        if (add.Count > AdjustRecipe.MaxAdded)
        {
            return new AdjustIdeaResult.Invalid($"Add at most {AdjustRecipe.MaxAdded} things at a time.");
        }

        var view = await stock.ExecuteAsync(cancellationToken);
        var known = view.Aisles.SelectMany(a => a.Products.Select(p => (Aisle: a, Product: p))).ToList();
        var byId = known.ToDictionary(k => k.Product.ProductId, k => k.Product);

        // Only things the idea uses can be removed (by the name shown, its generic name, or the name the app sent).
        var remove = new List<RecipeIngredient>();
        foreach (var use in choices.Current.Uses)
        {
            var names = new[] { byId.GetValueOrDefault(use.ProductId)?.DisplayName, byId.GetValueOrDefault(use.ProductId)?.GenericName, use.Name }
                .OfType<string>().ToList();
            if (names.Count > 0 && choices.Remove.Any(r => names.Any(n => string.Equals(r.Trim(), n, StringComparison.OrdinalIgnoreCase))))
            {
                remove.Add(new RecipeIngredient(names[0], "", use.ProductId));
            }
        }

        var changes = new RecipeChanges(add, typed, remove, tweak);
        if (changes.IsEmpty)
        {
            return new AdjustIdeaResult.Invalid("Choose something to add, remove or tweak.");
        }

        if (known.Count == 0)
        {
            return new AdjustIdeaResult.NothingInStock();
        }

        if (add.FirstOrDefault(id => !byId.ContainsKey(id)) is var missing && missing != Guid.Empty)
        {
            return new AdjustIdeaResult.Invalid($"Product {missing} isn't in stock.");
        }

        if (!rateLimit.TryTake(user.ObjectId))
        {
            LogRateLimited();
            return new AdjustIdeaResult.TooManyRequests();
        }

        var context = new RecipeRequest(
            choices.Meal,
            false,
            [.. known.Select(k => new RecipeStockItem(k.Product.ProductId, k.Product.DisplayName, k.Product.Count,
                k.Product.DaysUntilSoonest, k.Product.UseFirst, k.Aisle.Name, k.Product.OpenedCount, k.Product.AnimalContent))],
            add,
            tweak,
            [],
            Diet: choices.Diet);
        var current = new QuickIdea(choices.Current.Title, choices.Current.How, choices.Current.Appliance, choices.Current.Minutes,
            [.. choices.Current.Uses.Select(u => u.ProductId).Where(byId.ContainsKey)], choices.Current.AlsoNeeded);
        var result = await generator.AdjustIdeaAsync(new IdeaAdjustmentRequest(context, current, changes), cancellationToken);
        if (result is not IdeaResult.Adjusted adjusted)
        {
            LogUnavailable(add.Count, typed.Count, remove.Count, tweak is not null, ((IdeaResult.Unavailable)result).Reason);
            return new AdjustIdeaResult.Unavailable();
        }

        var couldNotAdd = adjusted.CouldNotFitIn.Select(id => byId[id].GenericName ?? byId[id].DisplayName).Concat(adjusted.CouldNotAddTyped).ToList();
        var summary = new AdjustmentSummary(
            [.. add.Select(id => byId[id].GenericName ?? byId[id].DisplayName).Concat(typed)],
            [.. remove.Select(r => r.Name)],
            tweak);
        LogAdjusted(add.Count, typed.Count, remove.Count, tweak is not null, adjusted.Idea.Appliance, couldNotAdd.Count, adjusted.CouldNotRemove.Count);
        return new AdjustIdeaResult.Adjusted(SuggestRecipe.ToView(adjusted.Idea, byId) with { Diet = choices.Diet }, summary, couldNotAdd,
            adjusted.CouldNotRemove, adjusted.Warnings ?? []);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Idea adjusted: {Added} added from stock, {Typed} typed, {Removed} removed, tweak given {TweakGiven}, appliance {Appliance}; couldn't add {NotAdded}, couldn't remove {NotRemoved}.")]
    private partial void LogAdjusted(int added, int typed, int removed, bool tweakGiven, Appliance appliance, int notAdded, int notRemoved);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Idea adjustment unavailable: {Added} added, {Typed} typed, {Removed} removed, tweak given {TweakGiven} ({Reason}); the app says try again.")]
    private partial void LogUnavailable(int added, int typed, int removed, bool tweakGiven, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Idea adjustment refused: over the hourly limit.")]
    private partial void LogRateLimited();
}
