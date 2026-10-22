using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.SavedRecipes;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Tests.SavedRecipes;

internal sealed class FakeSavedRecipes : ISavedRecipeRepository
{
    private int etag;

    public Dictionary<Guid, Versioned<SavedRecipe>> Items { get; } = [];

    public Task<Versioned<SavedRecipe>?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.GetValueOrDefault(id));

    public Task<IReadOnlyList<SavedRecipe>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SavedRecipe>>([.. Items.Values.Select(v => v.Value)]);

    public Task<bool> TryCreateAsync(SavedRecipe recipe, CancellationToken cancellationToken)
    {
        if (Items.ContainsKey(recipe.Id))
        {
            return Task.FromResult(false);
        }

        Items[recipe.Id] = new Versioned<SavedRecipe>(recipe, $"e{++etag}");
        return Task.FromResult(true);
    }

    public Task<bool> TryReplaceAsync(Versioned<SavedRecipe> current, SavedRecipe updated, CancellationToken cancellationToken)
    {
        if (Items.GetValueOrDefault(updated.Id)?.ETag != current.ETag)
        {
            return Task.FromResult(false);
        }

        Items[updated.Id] = new Versioned<SavedRecipe>(updated, $"e{++etag}");
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.Remove(id));
}

// Saved recipes and Ready now ideas (#105).
public sealed class SavedRecipeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");
    private static readonly CurrentUser Sam = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Sam");

    private readonly FakeStockQueries queries = new();
    private readonly FakeProducts products = new();
    private readonly FakeSavedRecipes repository = new();
    private readonly FixedClock clock = new(Now);
    private readonly ListLogger<SavedRecipeUseCases> logs = new();

    private SavedRecipeUseCases UseCases() => new(repository, products, new GetStock(queries, products, TestSettings.Stock(), clock), clock, logs);

    private Guid Known(string name, string? generic = null, int inStock = 1, int? expiresInDays = null)
    {
        var id = Guid.NewGuid();
        var product = Product.Found(id, "5000354922831", new ProductDetails(name, null, null, null, []), Now, 1).WithManualAisle("other", Now);
        if (generic is not null)
        {
            product = product.WithManualGenericName(GenericName.Create(generic, "other", Now, "manual"), Now);
        }

        products.Products[id] = product;
        for (var i = 0; i < inStock; i++)
        {
            queries.Units.Add(new StockUnit(id, null, Now.AddDays(-1), expiresInDays is { } d ? Today.AddDays(d) : null));
        }

        return id;
    }

    private static SaveRecipeRequest Recipe(Guid id, string title, params SavedIngredient[] ingredients) =>
        new(id, SavedRecipeKinds.Recipe, title, RecipeMeal.Dinner, 25, 2, ingredients, [new("Cook it.")], [new("1 garlic clove")], null, null);

    [Fact]
    public async Task Saving_keeps_a_snapshot_with_the_generic_names_and_is_idempotent()
    {
        var penne = Known("Napolina Penne", generic: "Penne");
        var id = Guid.NewGuid();

        var first = await UseCases().SaveAsync(Recipe(id, "  Creamy penne ", new SavedIngredient("Penne", "200 g", penne, null)), Gary, CancellationToken.None);
        var again = await UseCases().SaveAsync(Recipe(id, "Something else", new SavedIngredient("Penne", "200 g", penne, null)), Sam, CancellationToken.None);

        var saved = Assert.IsType<SavedRecipeResult.Saved>(first).View.Recipe;
        Assert.Equal(("Creamy penne", "Creamy penne", "Penne", "Gary"), (saved.Title, saved.OriginalTitle, saved.Ingredients[0].GenericName, saved.SavedByName));
        Assert.Equal("Creamy penne", Assert.IsType<SavedRecipeResult.Saved>(again).View.Recipe.Title); // the first save stands
        Assert.Single(repository.Items);
    }

    [Fact]
    public async Task Ready_now_ideas_are_saved_with_how_and_appliance()
    {
        var result = await UseCases().SaveAsync(
            new SaveRecipeRequest(Guid.NewGuid(), SavedRecipeKinds.ReadyNow, "Beans on toast", RecipeMeal.Snack, 5, null,
                [new SavedIngredient("Baked beans", null, null, null)], [], [], "Microwave the beans; toast the bread.", "microwave"),
            Gary, CancellationToken.None);

        var saved = Assert.IsType<SavedRecipeResult.Saved>(result).View.Recipe;
        Assert.Equal((SavedRecipeKinds.ReadyNow, "microwave", (int?)null), (saved.Kind, saved.Appliance, saved.Servings));
    }

    [Theory]
    [InlineData("", "recipe", null)]
    [InlineData("A title", "cake", null)]
    [InlineData("A title", "readyNow", "hob")]
    public async Task Bad_saves_are_refused(string title, string kind, string? appliance)
    {
        var result = await UseCases().SaveAsync(
            new SaveRecipeRequest(Guid.NewGuid(), kind, title, RecipeMeal.Dinner, 10, null, [], [], [], null, appliance), Gary, CancellationToken.None);
        Assert.IsType<SavedRecipeResult.Invalid>(result);
    }

    [Fact]
    public async Task The_list_is_newest_first_and_checked_against_todays_stock()
    {
        var milk = Known("Asda Semi Skimmed Milk", generic: "Semi-skimmed milk", expiresInDays: 1); // use first
        var oldMilk = Guid.NewGuid(); // a product that's since been used up, but its generic name is in stock
        Known("Napolina Penne", generic: "Penne");
        await UseCases().SaveAsync(Recipe(Guid.NewGuid(), "Older",
            new SavedIngredient("Milk", "300 ml", oldMilk, "Semi-skimmed milk"),
            new SavedIngredient("penne", "200 g", null, null), // by name: matches the generic name
            new SavedIngredient("Halloumi", "225 g", null, null),
            new SavedIngredient("Oil, salt and pepper", "1 tbsp", null, null)), Gary, CancellationToken.None);
        clock.Now = Now.AddMinutes(5);
        await UseCases().SaveAsync(Recipe(Guid.NewGuid(), "Newer", new SavedIngredient("Milk", "1", milk, null)), Gary, CancellationToken.None);

        var list = await UseCases().ListAsync(CancellationToken.None);

        Assert.Equal(["Newer", "Older"], list.Select(v => v.Recipe.Title));
        var older = list[1];
        Assert.Equal([IngredientStatus.UseFirst, IngredientStatus.Have, IngredientStatus.Missing, IngredientStatus.Have], older.Statuses);
        Assert.Equal(["Halloumi"], older.Missing);
        Assert.Equal(["Semi-skimmed milk"], older.UsesUp);
        Assert.Empty(list[0].Missing);
    }

    [Theory]
    [InlineData("Salt", true)]
    [InlineData("salt and pepper", true)]
    [InlineData("Olive oil", true)]
    [InlineData("Freshly ground black pepper", true)]
    [InlineData("Boiling water", true)]
    [InlineData("Red pepper", false)]
    [InlineData("Peppers", false)]
    [InlineData("Sesame oil dressing", false)]
    public void Staples_are_always_had(string name, bool staple) => Assert.Equal(staple, StockStatusChecker.IsStaple(name));

    [Fact]
    public async Task Rename_and_note_are_shared_and_limited()
    {
        var id = Guid.NewGuid();
        await UseCases().SaveAsync(Recipe(id, "Traybake"), Gary, CancellationToken.None);

        var renamed = await UseCases().UpdateAsync(id, " Sam's traybake ", "Halve the chilli", Sam, CancellationToken.None);

        var recipe = Assert.IsType<SavedRecipeResult.Saved>(renamed).View.Recipe;
        Assert.Equal(("Sam's traybake", "Traybake", "Halve the chilli", Sam.ObjectId), (recipe.Title, recipe.OriginalTitle, recipe.Note, recipe.UpdatedBy));
        Assert.IsType<SavedRecipeResult.Invalid>(await UseCases().UpdateAsync(id, "", null, Sam, CancellationToken.None));
        Assert.IsType<SavedRecipeResult.Invalid>(await UseCases().UpdateAsync(id, new string('x', 81), null, Sam, CancellationToken.None));
        Assert.IsType<SavedRecipeResult.Invalid>(await UseCases().UpdateAsync(id, null, new string('x', 301), Sam, CancellationToken.None));
        Assert.IsType<SavedRecipeResult.Invalid>(await UseCases().UpdateAsync(id, null, null, Sam, CancellationToken.None));
        Assert.IsType<SavedRecipeResult.NotFound>(await UseCases().UpdateAsync(Guid.NewGuid(), "x", null, Sam, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_then_save_again_brings_it_back_as_undo()
    {
        var id = Guid.NewGuid();
        await UseCases().SaveAsync(Recipe(id, "Traybake"), Gary, CancellationToken.None);

        Assert.True(await UseCases().DeleteAsync(id, CancellationToken.None));
        Assert.False(await UseCases().DeleteAsync(id, CancellationToken.None));
        Assert.Null(await UseCases().GetAsync(id, CancellationToken.None));
        await UseCases().SaveAsync(Recipe(id, "Traybake"), Gary, CancellationToken.None);

        Assert.NotNull(await UseCases().GetAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task Logs_hold_no_titles_or_notes()
    {
        var id = Guid.NewGuid();
        await UseCases().SaveAsync(Recipe(id, "Secret curry"), Gary, CancellationToken.None);
        await UseCases().UpdateAsync(id, null, "secret note", Gary, CancellationToken.None);

        Assert.All(logs.Entries, e => Assert.DoesNotContain("secret", e.Message, StringComparison.OrdinalIgnoreCase));
    }
}
