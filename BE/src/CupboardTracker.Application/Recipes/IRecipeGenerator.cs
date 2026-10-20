namespace CupboardTracker.Application.Recipes;

public enum RecipeMeal
{
    Breakfast,
    Lunch,
    Dinner,
    Snack,
}

/// <summary>Hungry? (#88): a proper recipe, or quick ideas made with a toaster, microwave or kettle, or no cooking.</summary>
public enum RecipeMode
{
    Cook,
    ReadyNow,
}

/// <summary>The only kitchen kit a Ready now idea may need.</summary>
public enum Appliance
{
    Toaster,
    Microwave,
    Kettle,
    None,
}

/// <summary>A Ready now idea (#88), already validated against the stock that was sent: the products it uses, a line or
/// two on how, and at most one thing that isn't in stock.</summary>
public sealed record QuickIdea(string Title, string How, Appliance Appliance, int Minutes, IReadOnlyList<Guid> Uses, string? AlsoNeeded);

public abstract record QuickIdeasResult
{
    public sealed record Suggested(IReadOnlyList<QuickIdea> Ideas) : QuickIdeasResult;

    public sealed record Unavailable(string Reason) : QuickIdeasResult;
}

/// <summary>One in-stock known product, as the recipe generator sees it (#73). Built on the server from Cosmos, never
/// from the app.</summary>
public sealed record RecipeStockItem(
    Guid ProductId, string Name, int Quantity, int? DaysUntilSoonest, bool UseFirst, string AisleName, int OpenedCount);

/// <summary>What to ask for: the meal, the stock, and the household's choices. <see cref="Comments"/> are one-off
/// preferences ("vegetarian", "no oven"), never instructions.</summary>
public sealed record RecipeRequest(
    RecipeMeal Meal,
    bool PreferUseFirst,
    IReadOnlyList<RecipeStockItem> Stock,
    IReadOnlyList<Guid> IncludeProductIds,
    string? Comments,
    IReadOnlyList<string> AvoidTitles);

/// <summary>An ingredient: <see cref="ProductId"/> when it's from the stock, <c>null</c> for a basic staple.</summary>
public sealed record RecipeIngredient(string Name, string Amount, Guid? ProductId);

/// <summary>A recipe suggestion, already validated against the stock that was sent. Nothing about it is stored.</summary>
public sealed record Recipe(
    string Title,
    string Summary,
    RecipeMeal Meal,
    int Servings,
    int TotalMinutes,
    IReadOnlyList<RecipeIngredient> Ingredients,
    IReadOnlyList<string> Steps,
    IReadOnlyList<Guid> UsesUp,
    IReadOnlyList<string> AlsoNeeded,
    IReadOnlyList<Guid> CouldNotFitIn);

public abstract record RecipeResult
{
    public sealed record Suggested(Recipe Recipe) : RecipeResult;

    /// <summary>The model couldn't be reached, timed out or refused (e.g. its content filter): the app says "try again".</summary>
    public sealed record Unavailable(string Reason) : RecipeResult;
}

/// <summary>Suggests a recipe from the stock (gpt-4o on Foundry in Infrastructure). The prompt, comments, stock and
/// recipe are never logged.</summary>
public interface IRecipeGenerator
{
    Task<RecipeResult> SuggestAsync(RecipeRequest request, CancellationToken cancellationToken);

    /// <summary>3 to 5 Ready now ideas (#88) from the same stock and choices.</summary>
    Task<QuickIdeasResult> SuggestQuickIdeasAsync(RecipeRequest request, CancellationToken cancellationToken);
}
