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

/// <summary>A recipe suggestion, already validated against the stock that was sent. Nothing about it is stored.
/// After an adjustment (#104), <see cref="CouldNotAddTyped"/> and <see cref="CouldNotRemove"/> name what the model
/// still got wrong after one retry (added products it left out are in <see cref="CouldNotFitIn"/>).</summary>
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
    IReadOnlyList<Guid> CouldNotFitIn,
    IReadOnlyList<string>? CouldNotAddTyped = null,
    IReadOnlyList<string>? CouldNotRemove = null);

/// <summary>
/// What to change in a recipe (#104): in-stock products to add, things to add that aren't in stock (they go under
/// "You'll also need"), ingredients to take out, and a free-text tweak ("spicier", "for 4"). The tweak is a
/// preference, never an instruction, like the form's comments.
/// </summary>
public sealed record RecipeChanges(
    IReadOnlyList<Guid> AddProductIds, IReadOnlyList<string> AddTyped, IReadOnlyList<RecipeIngredient> Remove, string? Tweak)
{
    public bool IsEmpty => AddProductIds.Count == 0 && AddTyped.Count == 0 && Remove.Count == 0 && Tweak is null;
}

/// <summary>An adjustment (#104): the stock and meal as for a suggestion (<see cref="RecipeRequest.IncludeProductIds"/>
/// are the products to add, <see cref="RecipeRequest.Comments"/> the tweak), the recipe as the app last showed it, and
/// the changes.</summary>
public sealed record RecipeAdjustmentRequest(RecipeRequest Context, Recipe Current, RecipeChanges Changes);

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

    /// <summary>The same recipe, updated with the changes (#104): added items in, removed ones out, the same dish where
    /// possible.</summary>
    Task<RecipeResult> AdjustAsync(RecipeAdjustmentRequest request, CancellationToken cancellationToken);
}
