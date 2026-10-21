using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Recipes;

// Adjust a recipe (#104): the use case and the suggested additions.
public sealed class AdjustRecipeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FakeStockQueries queries = new();
    private readonly FakeProducts products = new();
    private readonly FakeRecipeGenerator generator = new();
    private readonly ListLogger<AdjustRecipe> logger = new();
    private readonly FixedClock clock = new(Now);
    private readonly RecipeRateLimit rateLimit;

    public AdjustRecipeTests() => rateLimit = new RecipeRateLimit(new RecipeSettings(RequestsPerHour: 2), clock);

    private GetStock Stock() => new(queries, products, TestSettings.Stock(), clock);

    private AdjustRecipe UseCase() => new(Stock(), generator, rateLimit, logger);

    private Guid Known(string name, string? generic = null, int? expiresInDays = null)
    {
        var id = Guid.NewGuid();
        var product = Product.Found(id, "5000354922831", new ProductDetails(name, null, null, null, []), Now, 1).WithManualAisle("other", Now);
        if (generic is not null)
        {
            product = product.WithManualGenericName(GenericName.Create(generic, "other", Now, "manual"), Now);
        }

        products.Products[id] = product;
        queries.Units.Add(new StockUnit(id, null, Now.AddHours(-1), expiresInDays is { } d ? Today.AddDays(d) : null));
        return id;
    }

    private static Recipe Current(params RecipeIngredient[] ingredients) =>
        new("Creamy chicken penne", "A quick pasta.", RecipeMeal.Dinner, 2, 25, ingredients,
            ["Cook the penne.", "Fry the chicken until cooked through.", "Stir in the cheddar."], [], ["1 garlic clove"], []);

    private static AdjustChoices Choices(
        Recipe current, IReadOnlyList<Guid>? add = null, IReadOnlyList<string>? typed = null, IReadOnlyList<string>? remove = null, string? tweak = null) =>
        new(current, add ?? [], typed ?? [], remove ?? [], tweak);

    // ---- Adjust

    [Fact]
    public async Task Sends_the_current_recipe_the_changes_and_the_stock_rebuilt_from_cosmos()
    {
        var chicken = Known("Chicken breast");
        var chorizo = Known("Chorizo Sausage", generic: "Chorizo");
        var current = Current(new RecipeIngredient("Chicken breast", "2", chicken), new RecipeIngredient("Onion", "1", null));

        var result = await UseCase().ExecuteAsync(
            Choices(current, add: [chorizo], typed: [" chilli flakes "], remove: ["onion", "lobster"], tweak: "  a bit spicier "), Gary, CancellationToken.None);

        var sent = Assert.Single(generator.Adjustments);
        Assert.Same(current, sent.Current);
        Assert.Equal([chorizo], sent.Changes.AddProductIds);
        Assert.Equal(["chilli flakes"], sent.Changes.AddTyped);
        Assert.Equal(["Onion"], sent.Changes.Remove.Select(r => r.Name)); // "lobster" isn't in the recipe: ignored
        Assert.Equal("a bit spicier", sent.Changes.Tweak);
        Assert.Equal("a bit spicier", sent.Context.Comments);
        Assert.Equal([chorizo], sent.Context.IncludeProductIds);
        Assert.Equal(["Chicken breast", "Chorizo Sausage"], sent.Context.Stock.Select(s => s.Name).Order());
        var adjusted = Assert.IsType<AdjustRecipeResult.Adjusted>(result);
        Assert.Equal(["Chorizo", "chilli flakes"], adjusted.Summary.Added); // the generic name for a product
        Assert.Equal(["Onion"], adjusted.Summary.Removed);
        Assert.Equal("a bit spicier", adjusted.Summary.Tweak);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Nothing_to_change_is_refused(string? tweak)
    {
        var current = Current(new RecipeIngredient("Onion", "1", null));
        var result = await UseCase().ExecuteAsync(Choices(current, remove: ["lobster"], tweak: tweak), Gary, CancellationToken.None);
        Assert.Equal("Choose something to add, remove or tweak.", Assert.IsType<AdjustRecipeResult.Invalid>(result).Error);
        Assert.Empty(generator.Adjustments);
    }

    [Fact]
    public async Task Limits_on_the_tweak_typed_items_and_stock()
    {
        Known("Penne");
        var current = Current(new RecipeIngredient("Onion", "1", null));

        Assert.IsType<AdjustRecipeResult.Invalid>(await UseCase().ExecuteAsync(Choices(current, tweak: new string('x', 301)), Gary, CancellationToken.None));
        Assert.IsType<AdjustRecipeResult.Invalid>(await UseCase().ExecuteAsync(Choices(current, typed: ["a", "b", "c", "d"]), Gary, CancellationToken.None));
        Assert.IsType<AdjustRecipeResult.Invalid>(await UseCase().ExecuteAsync(Choices(current, typed: [new string('x', 41)]), Gary, CancellationToken.None));
        var notInStock = Guid.NewGuid();
        Assert.Equal($"Product {notInStock} isn't in stock.",
            Assert.IsType<AdjustRecipeResult.Invalid>(await UseCase().ExecuteAsync(Choices(current, add: [notInStock]), Gary, CancellationToken.None)).Error);
        Assert.IsType<AdjustRecipeResult.Adjusted>(await UseCase().ExecuteAsync(Choices(current, tweak: new string('x', 300)), Gary, CancellationToken.None));
    }

    [Fact]
    public async Task Adjustments_share_the_hourly_limit_with_suggestions()
    {
        Known("Penne");
        var current = Current(new RecipeIngredient("Onion", "1", null));
        var suggest = new SuggestRecipe(Stock(), generator, rateLimit, new ListLogger<SuggestRecipe>());

        await suggest.ExecuteAsync(new RecipeChoices(RecipeMeal.Dinner, false, [], null, []), Gary, CancellationToken.None);
        Assert.IsType<AdjustRecipeResult.Adjusted>(await UseCase().ExecuteAsync(Choices(current, tweak: "spicier"), Gary, CancellationToken.None));

        Assert.IsType<AdjustRecipeResult.TooManyRequests>(await UseCase().ExecuteAsync(Choices(current, tweak: "spicier"), Gary, CancellationToken.None));
    }

    [Fact]
    public async Task What_the_model_couldnt_do_is_shown_as_couldnt_add_and_couldnt_remove()
    {
        var chorizo = Known("Chorizo");
        var current = Current(new RecipeIngredient("Onion", "1", null));
        generator.Adjusted = r => new RecipeResult.Suggested(r.Current with
        {
            CouldNotFitIn = [chorizo],
            CouldNotAddTyped = ["chilli flakes"],
            CouldNotRemove = ["Onion"],
        });

        var result = await UseCase().ExecuteAsync(Choices(current, add: [chorizo], typed: ["chilli flakes"], remove: ["Onion"]), Gary, CancellationToken.None);

        var recipe = Assert.IsType<AdjustRecipeResult.Adjusted>(result).Recipe;
        Assert.Equal(["Chorizo", "chilli flakes"], recipe.CouldNotAdd!);
        Assert.Equal(["Onion"], recipe.CouldNotRemove!);
        Assert.Empty(recipe.CouldNotFitIn);
    }

    [Fact]
    public async Task The_model_being_unavailable_is_a_friendly_try_again_and_only_metadata_is_logged()
    {
        Known("Penne");
        generator.Adjusted = _ => new RecipeResult.Unavailable("timeout");

        var result = await UseCase().ExecuteAsync(
            Choices(Current(new RecipeIngredient("Secret onion", "1", null)), remove: ["Secret onion"], tweak: "my secret tweak"), Gary, CancellationToken.None);

        Assert.IsType<AdjustRecipeResult.Unavailable>(result);
        var line = Assert.Single(logger.Entries).Message;
        Assert.Contains("1 removed, tweak given True", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Suggested additions

    private sealed class FakeRanker : IRecipeAdditionRanker
    {
        public List<IReadOnlyList<string>> Asked { get; } = [];

        public Func<IReadOnlyList<string>, AdditionRanking> Answer { get; set; } = o => new AdditionRanking.Ranked([.. o.Select(_ => 0.0)], 1);

        public Task<AdditionRanking> RankAsync(RecipeSketch recipe, IReadOnlyList<string> options, CancellationToken cancellationToken)
        {
            Asked.Add(options);
            return Task.FromResult(Answer(options));
        }
    }

    private readonly FakeRanker ranker = new();

    private SuggestRecipeAdditions Additions(int batchSize = 254, double min = 0.1) =>
        new(Stock(), ranker, new RecipeAdditionSettings(min, batchSize), new ListLogger<SuggestRecipeAdditions>());

    /// <summary>Jev's answer, by option name.</summary>
    private static Func<IReadOnlyList<string>, AdditionRanking> Probabilities(Dictionary<string, double> p, double nothing = 0.05) =>
        options => new AdditionRanking.Ranked([.. options.Select(o => p.GetValueOrDefault(o))], nothing);

    [Fact]
    public async Task Candidates_are_in_stock_grouped_by_generic_name_and_leave_out_what_the_recipe_has()
    {
        var chicken = Known("Chicken Breast Fillets");
        Known("Asda Spinach", generic: "Spinach");
        Known("Tesco Baby Spinach", generic: "Spinach");
        Known("Mature Cheddar");
        Known("Chorizo Ring", generic: "Chorizo");
        var current = Current(new RecipeIngredient("Chicken breast", "2", chicken), new RecipeIngredient("Cheddar", "80 g", null));

        await Additions().ExecuteAsync(current, CancellationToken.None);

        Assert.Equal(["Chorizo", "Spinach"], Assert.Single(ranker.Asked)); // chicken by product, cheddar by name
    }

    [Fact]
    public async Task Up_to_3_above_the_threshold_and_better_than_nothing_best_first()
    {
        foreach (var name in new[] { "Chorizo", "Spinach", "Parmesan", "Peas", "Custard" })
        {
            Known(name);
        }

        ranker.Answer = Probabilities(new() { ["Chorizo"] = 0.4, ["Spinach"] = 0.25, ["Parmesan"] = 0.15, ["Peas"] = 0.12, ["Custard"] = 0.01 }, nothing: 0.13);

        var suggestions = await Additions().ExecuteAsync(Current(new RecipeIngredient("Penne", "200 g", null)), CancellationToken.None);

        Assert.Equal(["Chorizo", "Spinach", "Parmesan"], suggestions.Select(s => s.Name)); // Peas is below "Nothing"
        Assert.Equal(0.4, suggestions[0].Confidence);
    }

    [Fact]
    public async Task Nothing_would_improve_it_gives_no_suggestions()
    {
        Known("Custard");
        ranker.Answer = Probabilities(new() { ["Custard"] = 0.2 }, nothing: 0.8);

        Assert.Empty(await Additions().ExecuteAsync(Current(new RecipeIngredient("Penne", "200 g", null)), CancellationToken.None));
    }

    [Fact]
    public async Task Use_first_items_come_first_among_close_suggestions()
    {
        Known("Chorizo");
        var spinach = Known("Spinach", expiresInDays: 1);
        Known("Peas");
        ranker.Answer = Probabilities(new() { ["Chorizo"] = 0.35, ["Spinach"] = 0.3, ["Peas"] = 0.1 });

        var suggestions = await Additions().ExecuteAsync(Current(new RecipeIngredient("Penne", "200 g", null)), CancellationToken.None);

        Assert.Equal(["Spinach", "Chorizo", "Peas"], suggestions.Select(s => s.Name));
        Assert.Equal([spinach], suggestions[0].ProductIds);
        Assert.True(suggestions[0].UseFirst);
    }

    [Fact]
    public async Task A_use_first_item_well_behind_isnt_moved_up()
    {
        Known("Chorizo");
        Known("Spinach", expiresInDays: 1);
        ranker.Answer = Probabilities(new() { ["Chorizo"] = 0.6, ["Spinach"] = 0.2 });

        var suggestions = await Additions().ExecuteAsync(Current(new RecipeIngredient("Penne", "200 g", null)), CancellationToken.None);

        Assert.Equal(["Chorizo", "Spinach"], suggestions.Select(s => s.Name));
    }

    [Fact]
    public async Task More_candidates_than_a_batch_are_asked_in_batches_then_a_run_off()
    {
        foreach (var name in new[] { "Anchovies", "Bacon", "Chorizo", "Dill", "Egg", "Feta", "Garlic" })
        {
            Known(name);
        }

        var p = new Dictionary<string, double> { ["Bacon"] = 0.5, ["Chorizo"] = 0.3, ["Feta"] = 0.6, ["Garlic"] = 0.2 };
        ranker.Answer = options => options.Contains("Bacon") && options.Contains("Feta") // only the run-off has both
            ? Probabilities(new() { ["Feta"] = 0.2, ["Bacon"] = 0.5, ["Chorizo"] = 0.25 })(options) // the run-off decides
            : Probabilities(p)(options);

        var suggestions = await Additions(batchSize: 3).ExecuteAsync(Current(new RecipeIngredient("Penne", "200 g", null)), CancellationToken.None);

        Assert.Equal(4, ranker.Asked.Count); // 3 batches (3, 3, 1) and the run-off
        Assert.Equal(["Bacon", "Chorizo", "Feta"], suggestions.Select(s => s.Name));
    }

    [Fact]
    public async Task Jev_unavailable_quietly_gives_none()
    {
        Known("Chorizo");
        ranker.Answer = _ => new AdditionRanking.Unavailable("timeout");

        Assert.Empty(await Additions().ExecuteAsync(Current(new RecipeIngredient("Penne", "200 g", null)), CancellationToken.None));
    }
}
