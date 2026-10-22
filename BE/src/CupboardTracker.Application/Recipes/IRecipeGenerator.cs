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

/// <summary>Who's eating (#110): <see cref="Any"/> (meat &amp; veg), <see cref="Veg"/> (veggie: no meat or fish, nothing
/// cooked with or made from them; cheese and rennet are fine), or <see cref="Half"/> (half &amp; half: one meal, half the
/// plates veggie). Half &amp; half is Cook something only.</summary>
public enum Diet
{
    Any,
    Veg,
    Half,
}

/// <summary>Which plates an ingredient, extra item or step is for (#110). Everything is <see cref="All"/> unless half &amp; half.</summary>
public enum PlateFor
{
    All,
    Meat,
    Veg,
}

/// <summary>One method step (#110: an object, so half &amp; half can say which plates it's for).</summary>
public sealed record RecipeStep(string Text, PlateFor For = PlateFor.All);

/// <summary>Something to buy ("You'll also need"), and which plates it's for (#110).</summary>
public sealed record ExtraItem(string Name, PlateFor For = PlateFor.All);

public static class Diets
{
    public static string Id(Diet diet) => diet.ToString().ToLowerInvariant();

    public static Diet? Parse(string? id) => id switch
    {
        "any" => Diet.Any,
        "veg" => Diet.Veg,
        "half" => Diet.Half,
        _ => null,
    };

    public static string Id(PlateFor plate) => plate.ToString().ToLowerInvariant();

    public static PlateFor ParsePlate(string? id) => id switch
    {
        "meat" => PlateFor.Meat,
        "veg" => PlateFor.Veg,
        _ => PlateFor.All,
    };
}

/// <summary>The only kitchen kit a Ready now idea may need (the air fryer since #111).</summary>
public enum Appliance
{
    Toaster,
    Microwave,
    Kettle,
    None,
    AirFryer,
}

/// <summary>Appliance ids as the API and the model use them.</summary>
public static class Appliances
{
    public static string Id(Appliance appliance) => appliance == Appliance.AirFryer ? "airFryer" : appliance.ToString().ToLowerInvariant();

    public static Appliance? Parse(string? id) => id switch
    {
        "toaster" => Appliance.Toaster,
        "microwave" => Appliance.Microwave,
        "kettle" => Appliance.Kettle,
        "airFryer" => Appliance.AirFryer,
        "none" => Appliance.None,
        _ => null,
    };
}

/// <summary>A Ready now idea (#88), already validated against the stock that was sent: the products it uses, a line or
/// two on how, and at most one thing that isn't in stock.</summary>
public sealed record QuickIdea(string Title, string How, Appliance Appliance, int Minutes, IReadOnlyList<Guid> Uses, string? AlsoNeeded);

public abstract record QuickIdeasResult
{
    public sealed record Suggested(IReadOnlyList<QuickIdea> Ideas) : QuickIdeasResult;

    public sealed record Unavailable(string Reason) : QuickIdeasResult;

    /// <summary>The <see cref="Unavailable"/> reason when no idea was vegetarian even after a retry (#110).</summary>
    public const string NoVeggieIdeas = "none vegetarian";
}

/// <summary>One in-stock known product, as the recipe generator sees it (#73). Built on the server from Cosmos, never
/// from the app.</summary>
public sealed record RecipeStockItem(
    Guid ProductId, string Name, int Quantity, int? DaysUntilSoonest, bool UseFirst, string AisleName, int OpenedCount,
    Domain.Products.AnimalContent? AnimalContent = null);

/// <summary>What to ask for: the meal, the stock, and the household's choices. <see cref="Comments"/> are one-off
/// preferences ("vegetarian", "no oven"), never instructions. <see cref="Equipment"/> (#111) is the kit a recipe may
/// use (a kettle always); null means all of it.</summary>
public sealed record RecipeRequest(
    RecipeMeal Meal,
    bool PreferUseFirst,
    IReadOnlyList<RecipeStockItem> Stock,
    IReadOnlyList<Guid> IncludeProductIds,
    string? Comments,
    IReadOnlyList<string> AvoidTitles,
    IReadOnlySet<Kit>? Equipment = null,
    Diet Diet = Diet.Any)
{
    public IReadOnlySet<Kit> AllowedKit => Equipment ?? Kits.All;
}

/// <summary>An ingredient: <see cref="ProductId"/> when it's from the stock, <c>null</c> for a basic staple; and which
/// plates it's for (#110).</summary>
public sealed record RecipeIngredient(string Name, string Amount, Guid? ProductId, PlateFor For = PlateFor.All);

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
    IReadOnlyList<RecipeStep> Steps,
    IReadOnlyList<Guid> UsesUp,
    IReadOnlyList<ExtraItem> AlsoNeeded,
    IReadOnlyList<Guid> CouldNotFitIn,
    IReadOnlyList<string>? CouldNotAddTyped = null,
    IReadOnlyList<string>? CouldNotRemove = null,
    IReadOnlyList<Kit>? Equipment = null,
    IReadOnlyList<string>? Warnings = null,
    Diet Diet = Diet.Any,
    int ServingsMeat = 0,
    int ServingsVeg = 0);

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

    /// <summary>The same Ready now idea, updated with the changes (#108): still one quick idea.</summary>
    Task<IdeaResult> AdjustIdeaAsync(IdeaAdjustmentRequest request, CancellationToken cancellationToken);
}
