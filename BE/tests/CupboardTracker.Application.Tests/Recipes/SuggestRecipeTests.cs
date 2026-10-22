using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Recipes;

internal sealed class FakeRecipeGenerator : IRecipeGenerator
{
    public List<RecipeRequest> Requests { get; } = [];

    public Func<RecipeRequest, RecipeResult>? Answer { get; set; }

    public Task<RecipeResult> SuggestAsync(RecipeRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Answer?.Invoke(request) ?? new RecipeResult.Suggested(Recipe([])));
    }

    public Func<RecipeRequest, QuickIdeasResult>? Ideas { get; set; }

    public List<RecipeRequest> IdeaRequests { get; } = [];

    public Task<QuickIdeasResult> SuggestQuickIdeasAsync(RecipeRequest request, CancellationToken cancellationToken)
    {
        IdeaRequests.Add(request);
        return Task.FromResult(Ideas?.Invoke(request) ?? new QuickIdeasResult.Suggested([]));
    }

    public List<RecipeAdjustmentRequest> Adjustments { get; } = [];

    public Func<RecipeAdjustmentRequest, RecipeResult>? Adjusted { get; set; }

    public Task<RecipeResult> AdjustAsync(RecipeAdjustmentRequest request, CancellationToken cancellationToken)
    {
        Adjustments.Add(request);
        return Task.FromResult(Adjusted?.Invoke(request) ?? new RecipeResult.Suggested(request.Current));
    }

    public List<IdeaAdjustmentRequest> IdeaAdjustments { get; } = [];

    public Func<IdeaAdjustmentRequest, IdeaResult>? IdeaAdjusted { get; set; }

    public Task<IdeaResult> AdjustIdeaAsync(IdeaAdjustmentRequest request, CancellationToken cancellationToken)
    {
        IdeaAdjustments.Add(request);
        return Task.FromResult(IdeaAdjusted?.Invoke(request) ?? new IdeaResult.Adjusted(request.Current, [], [], []));
    }

    public static Recipe Recipe(IReadOnlyList<RecipeIngredient> ingredients, IReadOnlyList<Guid>? usesUp = null, IReadOnlyList<Guid>? couldNotFitIn = null) =>
        new("Creamy penne", "A quick pasta.", RecipeMeal.Dinner, 2, 25, ingredients, [new("Cook the penne.")], usesUp ?? [], [new("1 onion")], couldNotFitIn ?? []);
}

public sealed class SuggestRecipeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 17);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FakeStockQueries queries = new();
    private readonly FakeProducts products = new();
    private readonly FakeRecipeGenerator generator = new();
    private readonly ListLogger<SuggestRecipe> logger = new();
    private readonly FixedClock clock = new(Now);
    private RecipeRateLimit rateLimit;

    public SuggestRecipeTests() => rateLimit = new RecipeRateLimit(new RecipeSettings(), clock);

    private SuggestRecipe UseCase() =>
        new(new GetStock(queries, products, TestSettings.Stock(), clock), generator, rateLimit, logger);

    private Guid Known(string name, int count = 1, int? expiresInDays = null, string? aisle = null, bool opened = false)
    {
        var id = Guid.NewGuid();
        var product = Product.Found(id, "5000354922831", new ProductDetails(name, null, null, null, []), Now, 1);
        products.Products[id] = aisle is null ? product : product.WithManualAisle(aisle, Now);
        for (var i = 0; i < count; i++)
        {
            queries.Units.Add(new StockUnit(id, null, Now.AddHours(-1), expiresInDays is { } d ? Today.AddDays(d) : null,
                OpenedOn: opened && i == 0 ? Today : null));
        }

        return id;
    }

    private Guid Unnamed()
    {
        var id = Guid.NewGuid();
        products.Products[id] = Product.NotFound(id, "5012345678900", Now, 1);
        queries.Units.Add(new StockUnit(id, "5012345678900", Now.AddHours(-1)));
        return id;
    }

    private static RecipeChoices Choices(
        RecipeMeal meal = RecipeMeal.Dinner, bool prefer = true, IReadOnlyList<Guid>? include = null, string? comments = null,
        IReadOnlyList<string>? avoid = null, RecipeMode mode = RecipeMode.Cook) =>
        new(meal, prefer, include ?? [], comments, avoid ?? [], mode);

    // ---- Ready now (#88)

    [Fact]
    public async Task Ready_now_asks_for_ideas_not_a_recipe_and_returns_them_with_their_products()
    {
        var bread = Known("Sourdough loaf", expiresInDays: 5);
        var cheese = Known("Mature cheddar", expiresInDays: 20);
        generator.Ideas = _ => new QuickIdeasResult.Suggested(
        [
            new QuickIdea("Cheese on toast", "Toast 2 slices, add cheddar, microwave 30 s.", Appliance.Toaster, 5, [bread, cheese], null),
        ]);

        var result = await UseCase().ExecuteAsync(Choices(RecipeMeal.Snack, mode: RecipeMode.ReadyNow), Gary, CancellationToken.None);

        var ideas = Assert.IsType<SuggestRecipeResult.IdeasSuggested>(result);
        Assert.Equal(RecipeMeal.Snack, ideas.Meal);
        var idea = Assert.Single(ideas.Ideas);
        Assert.Equal(("Cheese on toast", Appliance.Toaster, 5), (idea.Title, idea.Appliance, idea.Minutes));
        Assert.Equal(["Sourdough loaf", "Mature cheddar"], idea.Uses.Select(u => u.DisplayName));
        Assert.Equal(RecipeMeal.Snack, Assert.Single(generator.IdeaRequests).Meal);
        Assert.Empty(generator.Requests); // no recipe asked for
        Assert.Contains(logger.Entries, e => e.Message.StartsWith("Ready now ideas suggested: meal Snack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_prefer_use_first_ideas_using_use_first_items_come_first_otherwise_the_models_order()
    {
        var milk = Known("Semi-skimmed milk", expiresInDays: 1);
        var oats = Known("Rolled oats", expiresInDays: 200);
        QuickIdea Idea(string title, Guid uses) => new(title, "How.", Appliance.Microwave, 3, [uses], null);
        generator.Ideas = _ => new QuickIdeasResult.Suggested([Idea("Porridge", oats), Idea("Hot milk", milk), Idea("Oat bar", oats)]);

        var preferred = (SuggestRecipeResult.IdeasSuggested)await UseCase().ExecuteAsync(
            Choices(RecipeMeal.Breakfast, prefer: true, mode: RecipeMode.ReadyNow), Gary, CancellationToken.None);
        var plain = (SuggestRecipeResult.IdeasSuggested)await UseCase().ExecuteAsync(
            Choices(RecipeMeal.Breakfast, prefer: false, mode: RecipeMode.ReadyNow), Gary, CancellationToken.None);

        Assert.Equal(["Hot milk", "Porridge", "Oat bar"], preferred.Ideas.Select(i => i.Title));
        Assert.True(preferred.Ideas[0].Uses[0].UseFirst);
        Assert.Equal(["Porridge", "Hot milk", "Oat bar"], plain.Ideas.Select(i => i.Title));
    }

    [Fact]
    public async Task Ready_now_unavailable_is_logged_with_the_mode_and_meal_and_no_text()
    {
        Known("Eggs");
        generator.Ideas = _ => new QuickIdeasResult.Unavailable("timeout");

        var result = await UseCase().ExecuteAsync(
            Choices(RecipeMeal.Lunch, comments: "nothing with eggs please", mode: RecipeMode.ReadyNow), Gary, CancellationToken.None);

        Assert.IsType<SuggestRecipeResult.Unavailable>(result);
        var entry = Assert.Single(logger.Entries, e => e.Message.StartsWith("Recipe unavailable", StringComparison.Ordinal));
        Assert.Contains("mode ReadyNow, meal Lunch", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("eggs please", StringComparison.Ordinal));
    }

    [Fact]
    public async Task More_ideas_passes_up_to_20_shown_titles()
    {
        Known("Eggs");
        var titles = Enumerable.Range(1, 25).Select(i => $"Idea {i}").ToList();

        await UseCase().ExecuteAsync(Choices(avoid: titles, mode: RecipeMode.ReadyNow), Gary, CancellationToken.None);

        Assert.Equal(titles.Skip(5), Assert.Single(generator.IdeaRequests).AvoidTitles);
    }

    [Fact]
    public async Task The_stock_sent_is_built_from_cosmos_with_known_products_only()
    {
        var milk = Known("Semi-skimmed milk", count: 2, expiresInDays: 2, aisle: "dairyEggs", opened: true);
        var penne = Known("Wholewheat penne", expiresInDays: 300, aisle: "pastaRiceGrains");
        Unnamed();

        Assert.IsType<SuggestRecipeResult.Suggested>(await UseCase().ExecuteAsync(Choices(), Gary, CancellationToken.None));

        var request = Assert.Single(generator.Requests);
        Assert.Equal(
            [
                new RecipeStockItem(milk, "Semi-skimmed milk", 2, 2, true, "Dairy & eggs", 1),
                new RecipeStockItem(penne, "Wholewheat penne", 1, 300, false, Aisles.Of("pastaRiceGrains").Name, 0),
            ],
            request.Stock.OrderBy(s => s.Name));
        Assert.Equal((RecipeMeal.Dinner, true), (request.Meal, request.PreferUseFirst));
    }

    [Fact]
    public async Task Comments_are_trimmed_and_empty_ones_are_none()
    {
        Known("Eggs");
        await UseCase().ExecuteAsync(Choices(comments: "  something spicy  "), Gary, CancellationToken.None);
        await UseCase().ExecuteAsync(Choices(comments: "   "), Gary, CancellationToken.None);

        Assert.Equal(["something spicy", null], generator.Requests.Select(r => r.Comments));
    }

    [Fact]
    public async Task Comments_over_300_characters_are_refused()
    {
        Known("Eggs");
        var result = await UseCase().ExecuteAsync(Choices(comments: new string('a', 301)), Gary, CancellationToken.None);

        Assert.Equal("Comments can be at most 300 characters.", Assert.IsType<SuggestRecipeResult.Invalid>(result).Error);
        Assert.Empty(generator.Requests);
        Assert.IsType<SuggestRecipeResult.Suggested>(
            await UseCase().ExecuteAsync(Choices(comments: new string('a', 300)), Gary, CancellationToken.None));
    }

    [Fact]
    public async Task An_included_product_must_be_known_and_in_stock()
    {
        Known("Eggs");
        var unnamed = Unnamed();

        Assert.IsType<SuggestRecipeResult.Invalid>(await UseCase().ExecuteAsync(Choices(include: [unnamed]), Gary, CancellationToken.None));
        Assert.IsType<SuggestRecipeResult.Invalid>(await UseCase().ExecuteAsync(Choices(include: [Guid.NewGuid()]), Gary, CancellationToken.None));
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public async Task Nothing_known_in_stock_is_its_own_answer()
    {
        Unnamed();
        Assert.IsType<SuggestRecipeResult.NothingInStock>(await UseCase().ExecuteAsync(Choices(), Gary, CancellationToken.None));
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public async Task Included_products_and_avoided_titles_are_passed_through()
    {
        var eggs = Known("Eggs");
        var cheese = Known("Cheddar");

        await UseCase().ExecuteAsync(Choices(include: [eggs, cheese, eggs], avoid: [" Omelette ", "", "Frittata"]), Gary, CancellationToken.None);

        var request = Assert.Single(generator.Requests);
        Assert.Equal([eggs, cheese], request.IncludeProductIds);
        Assert.Equal(["Omelette", "Frittata"], request.AvoidTitles);
    }

    [Fact]
    public async Task The_recipe_names_in_stock_ingredients_by_their_current_name_with_use_first_details()
    {
        var milk = Known("Semi-skimmed milk", expiresInDays: 1);
        var penne = Known("Wholewheat penne");
        var gone = Guid.NewGuid();
        generator.Answer = _ => new RecipeResult.Suggested(FakeRecipeGenerator.Recipe(
            [new("milk", "300 ml", milk), new("pasta", "200 g", penne), new("Oil", "1 tbsp", null)],
            usesUp: [milk, gone], couldNotFitIn: [penne]));

        var result = await UseCase().ExecuteAsync(Choices(), Gary, CancellationToken.None);

        var recipe = Assert.IsType<SuggestRecipeResult.Suggested>(result).Recipe;
        Assert.Equal(
            [
                new RecipeIngredientView("Semi-skimmed milk", "300 ml", milk, true),
                new RecipeIngredientView("Wholewheat penne", "200 g", penne, false),
                new RecipeIngredientView("Oil", "1 tbsp", null, false),
            ],
            recipe.Ingredients);
        Assert.Equal([new RecipeProductView(milk, "Semi-skimmed milk", 1, Today.AddDays(1))], recipe.UsesUp);
        Assert.Equal(["Wholewheat penne"], recipe.CouldNotFitIn);
        Assert.Equal([new ExtraItem("1 onion")], recipe.AlsoNeeded);
    }

    [Fact]
    public async Task An_unavailable_model_is_reported_without_the_comments()
    {
        Known("Eggs");
        generator.Answer = _ => new RecipeResult.Unavailable("content filter");

        var result = await UseCase().ExecuteAsync(Choices(comments: "secret-ish preference text"), Gary, CancellationToken.None);

        Assert.IsType<SuggestRecipeResult.Unavailable>(result);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("content filter", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("secret-ish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_metadata_is_logged()
    {
        Known("Eggs");
        await UseCase().ExecuteAsync(Choices(comments: "no oven please"), Gary, CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("Recipe suggested: meal Dinner, comments given True, 0 included, 0 couldn't be fitted in, "
            + "equipment hob,ovenGrill,microwave,toaster,airFryer,slowCooker,riceCooker,soupMaker,blender, warning False, diet any.", entry.Message);
    }

    [Fact]
    public async Task Each_user_gets_30_requests_an_hour()
    {
        Known("Eggs");
        rateLimit = new RecipeRateLimit(new RecipeSettings(), clock);
        for (var i = 0; i < 30; i++)
        {
            Assert.IsType<SuggestRecipeResult.Suggested>(await UseCase().ExecuteAsync(Choices(), Gary, CancellationToken.None));
        }

        Assert.IsType<SuggestRecipeResult.TooManyRequests>(await UseCase().ExecuteAsync(Choices(), Gary, CancellationToken.None));
        Assert.IsType<SuggestRecipeResult.Suggested>(
            await UseCase().ExecuteAsync(Choices(), new CurrentUser(Guid.NewGuid(), "Someone else"), CancellationToken.None));
        Assert.Equal(31, generator.Requests.Count);

        clock.Now = Now.AddHours(1);
        Assert.IsType<SuggestRecipeResult.Suggested>(await UseCase().ExecuteAsync(Choices(), Gary, CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_requests_dont_count_against_the_limit()
    {
        Known("Eggs");
        rateLimit = new RecipeRateLimit(new RecipeSettings(RequestsPerHour: 1), clock);

        await UseCase().ExecuteAsync(Choices(comments: new string('a', 301)), Gary, CancellationToken.None);
        await UseCase().ExecuteAsync(Choices(include: [Guid.NewGuid()]), Gary, CancellationToken.None);

        Assert.IsType<SuggestRecipeResult.Suggested>(await UseCase().ExecuteAsync(Choices(), Gary, CancellationToken.None));
        Assert.IsType<SuggestRecipeResult.TooManyRequests>(await UseCase().ExecuteAsync(Choices(), Gary, CancellationToken.None));
    }
}
