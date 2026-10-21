using CupboardTracker.Application.Products;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.SavedRecipes;

public static class SavedRecipeKinds
{
    public const string Recipe = "recipe";
    public const string ReadyNow = "readyNow";
}

/// <summary>An ingredient of a saved recipe, or an item a saved Ready now idea uses (#105). The generic name is
/// recorded at save time, so it can still be matched to stock after that product is used up.</summary>
public sealed record SavedIngredient(string Name, string? Amount, Guid? ProductId, string? GenericName);

/// <summary>
/// A recipe or Ready now idea the household saved (#105): a snapshot of what was shown, never regenerated. One shared
/// collection; anyone can rename it or change its note. Recipes have servings and steps; Ready now ideas have
/// <see cref="How"/> and <see cref="Appliance"/>, and their <see cref="Ingredients"/> are the items they use.
/// </summary>
public sealed record SavedRecipe(
    Guid Id,
    string Kind,
    string Title,
    string OriginalTitle,
    RecipeMeal Meal,
    int TotalMinutes,
    int? Servings,
    IReadOnlyList<SavedIngredient> Ingredients,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> AlsoNeeded,
    string? How,
    string? Appliance,
    string Note,
    Guid SavedBy,
    string SavedByName,
    DateTimeOffset SavedAt,
    Guid UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>The savedRecipes container (#105): one household partition.</summary>
public interface ISavedRecipeRepository
{
    Task<Versioned<SavedRecipe>?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SavedRecipe>> ListAsync(CancellationToken cancellationToken);

    /// <summary>False if one with that id exists already (a retry, or Undo of a delete that already came back).</summary>
    Task<bool> TryCreateAsync(SavedRecipe recipe, CancellationToken cancellationToken);

    /// <summary>False if it changed (or went) since it was read.</summary>
    Task<bool> TryReplaceAsync(Versioned<SavedRecipe> current, SavedRecipe updated, CancellationToken cancellationToken);

    /// <summary>False if there was nothing to delete.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Whether an ingredient is in today's stock (#105).</summary>
public enum IngredientStatus
{
    Have,
    Missing,
    UseFirst,
}

/// <summary>A saved recipe checked against today's stock: each ingredient's status, the missing names and the
/// use-first ones it would use up.</summary>
public sealed record SavedRecipeView(
    SavedRecipe Recipe, IReadOnlyList<IngredientStatus> Statuses, IReadOnlyList<string> Missing, IReadOnlyList<string> UsesUp);

/// <summary>
/// Checks saved recipes against today's stock (#105). An ingredient is had when its product is in stock; otherwise
/// when any in-stock product shares its generic name; otherwise when its name matches an in-stock product's generic or
/// display name. Basic staples (salt, pepper, oil, water) are always had. It's use first when it's had and a matching
/// product is use first.
/// </summary>
public sealed class StockStatusChecker
{
    private static readonly HashSet<string> StapleWords = new(StringComparer.Ordinal)
    {
        "salt", "pepper", "black", "white", "ground", "freshly", "sea", "oil", "olive", "vegetable", "sunflower", "rapeseed",
        "cooking", "extra", "virgin", "water", "boiling", "cold", "warm", "hot", "and", "to", "taste", "a", "pinch", "of",
        "for", "frying", "drizzle", "splash", "some", "little", "seasoning",
    };

    private readonly HashSet<Guid> inStock;
    private readonly Dictionary<Guid, bool> useFirstById;
    private readonly Dictionary<string, bool> byGeneric = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> byName = new(StringComparer.Ordinal);

    public StockStatusChecker(IEnumerable<StockProduct> stock)
    {
        var products = stock.ToList();
        inStock = [.. products.Select(p => p.ProductId)];
        useFirstById = products.ToDictionary(p => p.ProductId, p => p.UseFirst);
        foreach (var p in products)
        {
            if (p.GenericName is { } g)
            {
                Add(byGeneric, GenericNames.IdOf(g), p.UseFirst);
                Add(byName, GenericNames.IdOf(g), p.UseFirst);
            }

            Add(byName, GenericNames.IdOf(p.DisplayName), p.UseFirst);
        }
    }

    private static void Add(Dictionary<string, bool> map, string key, bool useFirst) =>
        map[key] = map.TryGetValue(key, out var already) ? already || useFirst : useFirst;

    public static bool IsStaple(string name)
    {
        var words = GenericNames.IdOf(name).Split([' ', ',', '/', '-', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(StapleWords.Contains);
    }

    public IngredientStatus StatusOf(SavedIngredient ingredient)
    {
        if (ingredient.ProductId is { } id && inStock.Contains(id))
        {
            return useFirstById[id] ? IngredientStatus.UseFirst : IngredientStatus.Have;
        }

        if (ingredient.GenericName is { } generic && byGeneric.TryGetValue(GenericNames.IdOf(generic), out var g))
        {
            return g ? IngredientStatus.UseFirst : IngredientStatus.Have;
        }

        if (byName.TryGetValue(GenericNames.IdOf(ingredient.Name), out var n))
        {
            return n ? IngredientStatus.UseFirst : IngredientStatus.Have;
        }

        return IsStaple(ingredient.Name) ? IngredientStatus.Have : IngredientStatus.Missing;
    }

    public SavedRecipeView Check(SavedRecipe recipe)
    {
        var statuses = recipe.Ingredients.Select(StatusOf).ToList();
        var named = recipe.Ingredients.Zip(statuses, (i, s) => (Name: i.GenericName ?? i.Name, Status: s)).ToList();
        return new SavedRecipeView(
            recipe,
            statuses,
            [.. named.Where(x => x.Status == IngredientStatus.Missing).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. named.Where(x => x.Status == IngredientStatus.UseFirst).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }
}

/// <summary>What the app sends to save (#105): what it was showing.</summary>
public sealed record SaveRecipeRequest(
    Guid Id,
    string Kind,
    string Title,
    RecipeMeal Meal,
    int TotalMinutes,
    int? Servings,
    IReadOnlyList<SavedIngredient> Ingredients,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> AlsoNeeded,
    string? How,
    string? Appliance,
    string? Note = null,
    string? OriginalTitle = null);

public abstract record SavedRecipeResult
{
    public sealed record Saved(SavedRecipeView View) : SavedRecipeResult;

    public sealed record Invalid(string Error) : SavedRecipeResult;

    public sealed record NotFound : SavedRecipeResult;

    public sealed record Conflict : SavedRecipeResult;
}

/// <summary>
/// Saved recipes (#105): save a snapshot (idempotent by id, so Undo of a delete is the same call), list them newest
/// first, get one, rename it or change its note, and delete it. Each comes back checked against today's stock. Only
/// metadata is logged: never titles, notes or recipe text.
/// </summary>
public sealed partial class SavedRecipeUseCases(
    ISavedRecipeRepository repository, IProductRepository products, GetStock stock, TimeProvider clock, ILogger<SavedRecipeUseCases> logger)
{
    public const int MaxTitle = 80;
    public const int MaxNote = 300;
    public const int MaxIngredients = 40;
    public const int MaxSteps = 20;
    public const int MaxAlsoNeeded = 5;
    public const int MaxText = 400;
    public const int MaxSaved = 500;

    public async Task<SavedRecipeResult> SaveAsync(SaveRecipeRequest request, CurrentUser user, CancellationToken cancellationToken)
    {
        if (Problem(request) is { } problem)
        {
            return new SavedRecipeResult.Invalid(problem);
        }

        if (await repository.GetAsync(request.Id, cancellationToken) is { } existing)
        {
            return new SavedRecipeResult.Saved(await CheckAsync(existing.Value, cancellationToken)); // a retry or a repeated tap
        }

        if ((await repository.ListAsync(cancellationToken)).Count >= MaxSaved)
        {
            return new SavedRecipeResult.Invalid($"That's {MaxSaved} saved already. Remove some first.");
        }

        var now = clock.GetUtcNow();
        var ingredients = new List<SavedIngredient>();
        foreach (var i in request.Ingredients)
        {
            var generic = i.GenericName;
            if (generic is null && i.ProductId is { } id)
            {
                generic = (await products.GetAsync(id, cancellationToken))?.Generic?.Name; // so it still matches once that product is used up
            }

            ingredients.Add(i with { Name = Cap(i.Name.Trim(), 100), Amount = i.Amount is null ? null : Cap(i.Amount.Trim(), 60), GenericName = generic });
        }

        var title = request.Title.Trim();
        var recipe = new SavedRecipe(
            request.Id, request.Kind, title, Cap((request.OriginalTitle ?? title).Trim(), MaxTitle), request.Meal,
            Math.Clamp(request.TotalMinutes, 1, 600), request.Kind == SavedRecipeKinds.Recipe ? Math.Clamp(request.Servings ?? 2, 1, 12) : null,
            ingredients,
            [.. request.Steps.Select(s => Cap(s.Trim(), MaxText)).Where(s => s.Length > 0)],
            [.. request.AlsoNeeded.Select(a => Cap(a.Trim(), 60)).Where(a => a.Length > 0)],
            request.How is null ? null : Cap(request.How.Trim(), MaxText),
            request.Appliance,
            Cap((request.Note ?? "").Trim(), MaxNote),
            user.ObjectId, user.DisplayName, now, user.ObjectId, now);
        if (!await repository.TryCreateAsync(recipe, cancellationToken))
        {
            var raced = await repository.GetAsync(request.Id, cancellationToken);
            return raced is null ? new SavedRecipeResult.Conflict() : new SavedRecipeResult.Saved(await CheckAsync(raced.Value, cancellationToken));
        }

        LogSaved(recipe.Id, recipe.Kind, recipe.Ingredients.Count);
        return new SavedRecipeResult.Saved(await CheckAsync(recipe, cancellationToken));
    }

    public async Task<IReadOnlyList<SavedRecipeView>> ListAsync(CancellationToken cancellationToken)
    {
        var all = await repository.ListAsync(cancellationToken);
        var checker = new StockStatusChecker(await InStockAsync(cancellationToken));
        return [.. all.OrderByDescending(r => r.SavedAt).ThenBy(r => r.Id).Select(checker.Check)];
    }

    public async Task<SavedRecipeView?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await repository.GetAsync(id, cancellationToken) is { } found ? await CheckAsync(found.Value, cancellationToken) : null;

    /// <summary>Rename and/or change the note (shared with the household).</summary>
    public async Task<SavedRecipeResult> UpdateAsync(Guid id, string? title, string? note, CurrentUser user, CancellationToken cancellationToken)
    {
        if (title is null && note is null)
        {
            return new SavedRecipeResult.Invalid("Send a title or a note.");
        }

        var newTitle = title?.Trim();
        if (newTitle is not null && newTitle.Length is 0 or > MaxTitle)
        {
            return new SavedRecipeResult.Invalid($"A title is 1 to {MaxTitle} characters.");
        }

        var newNote = note?.Trim();
        if (newNote?.Length > MaxNote)
        {
            return new SavedRecipeResult.Invalid($"A note is at most {MaxNote} characters.");
        }

        for (var round = 0; round < 5; round++)
        {
            if (await repository.GetAsync(id, cancellationToken) is not { } current)
            {
                return new SavedRecipeResult.NotFound();
            }

            var updated = current.Value with
            {
                Title = newTitle ?? current.Value.Title,
                Note = newNote ?? current.Value.Note,
                UpdatedBy = user.ObjectId,
                UpdatedAt = clock.GetUtcNow(),
            };
            if (await repository.TryReplaceAsync(current, updated, cancellationToken))
            {
                LogUpdated(id, newTitle is not null, newNote is not null);
                return new SavedRecipeResult.Saved(await CheckAsync(updated, cancellationToken));
            }
        }

        return new SavedRecipeResult.Conflict();
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await repository.DeleteAsync(id, cancellationToken);
        if (deleted)
        {
            LogDeleted(id);
        }

        return deleted;
    }

    private async Task<SavedRecipeView> CheckAsync(SavedRecipe recipe, CancellationToken cancellationToken) =>
        new StockStatusChecker(await InStockAsync(cancellationToken)).Check(recipe);

    private async Task<IEnumerable<StockProduct>> InStockAsync(CancellationToken cancellationToken) =>
        (await stock.ExecuteAsync(cancellationToken)).Aisles.SelectMany(a => a.Products);

    internal static string? Problem(SaveRecipeRequest r)
    {
        if (r.Id == Guid.Empty)
        {
            return "Send an id.";
        }

        if (r.Kind is not (SavedRecipeKinds.Recipe or SavedRecipeKinds.ReadyNow))
        {
            return "kind must be \"recipe\" or \"readyNow\".";
        }

        if (r.Title.Trim().Length is 0 or > MaxTitle)
        {
            return $"A title is 1 to {MaxTitle} characters.";
        }

        if (r.Ingredients.Count > MaxIngredients || r.Ingredients.Any(i => string.IsNullOrWhiteSpace(i.Name)))
        {
            return $"At most {MaxIngredients} ingredients, each with a name.";
        }

        if (r.Steps.Count > MaxSteps || r.AlsoNeeded.Count > MaxAlsoNeeded)
        {
            return $"At most {MaxSteps} steps and {MaxAlsoNeeded} things to buy.";
        }

        if (r.Note?.Trim().Length > MaxNote)
        {
            return $"A note is at most {MaxNote} characters.";
        }

        if (r.Kind == SavedRecipeKinds.ReadyNow && r.Appliance is not ("toaster" or "microwave" or "kettle" or "none"))
        {
            return "A Ready now idea needs its appliance.";
        }

        return null;
    }

    private static string Cap(string text, int max) => text.Length > max ? text[..max] : text;

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved recipe {Id} ({Kind}, {Ingredients} ingredients).")]
    private partial void LogSaved(Guid id, string kind, int ingredients);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved recipe {Id} changed (title {Title}, note {Note}).")]
    private partial void LogUpdated(Guid id, bool title, bool note);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved recipe {Id} removed.")]
    private partial void LogDeleted(Guid id);
}
