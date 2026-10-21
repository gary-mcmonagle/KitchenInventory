using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Recipes;

// Adjust a Ready now idea (#108).
public sealed class AdjustIdeaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FakeStockQueries queries = new();
    private readonly FakeProducts products = new();
    private readonly FakeRecipeGenerator generator = new();
    private readonly ListLogger<AdjustIdea> logger = new();
    private readonly FixedClock clock = new(Now);
    private readonly RecipeRateLimit rateLimit;

    public AdjustIdeaTests() => rateLimit = new RecipeRateLimit(new RecipeSettings(RequestsPerHour: 2), clock);

    private AdjustIdea UseCase() => new(new GetStock(queries, products, TestSettings.Stock(), clock), generator, rateLimit, logger);

    private Guid Known(string name, string? generic = null)
    {
        var id = Guid.NewGuid();
        var product = Product.Found(id, "5000354922831", new ProductDetails(name, null, null, null, []), Now, 1).WithManualAisle("other", Now);
        if (generic is not null)
        {
            product = product.WithManualGenericName(GenericName.Create(generic, "other", Now, "manual"), Now);
        }

        products.Products[id] = product;
        queries.Units.Add(new StockUnit(id, null, Now.AddHours(-1), null));
        return id;
    }

    private static IdeaAdjustChoices Choices(
        IReadOnlyList<IdeaUse> uses, IReadOnlyList<Guid>? add = null, IReadOnlyList<string>? typed = null, IReadOnlyList<string>? remove = null,
        string? tweak = null) =>
        new(new IdeaInput("Beans on toast", "Microwave the beans; toast the bread.", Appliance.Microwave, 5, uses, null),
            RecipeMeal.Snack, add ?? [], typed ?? [], remove ?? [], tweak);

    [Fact]
    public async Task Sends_the_idea_and_changes_with_the_stock_rebuilt_and_returns_it_as_shown_with_the_summary()
    {
        var beans = Known("Heinz Baked Beanz", generic: "Baked beans");
        var bread = Known("Hovis Wholemeal");
        var cheddar = Known("Tesco Mature Cheddar", generic: "Mature cheddar");
        generator.IdeaAdjusted = r => new IdeaResult.Adjusted(
            new QuickIdea("Cheesy beans on toast", "Melt the cheddar over the beans.", Appliance.Microwave, 6, [beans, bread, cheddar], null), [], [], []);

        var result = await UseCase().ExecuteAsync(
            Choices([new IdeaUse(beans, "Heinz Baked Beanz"), new IdeaUse(bread, null)], add: [cheddar], remove: ["baked beans", "lobster"], tweak: " spicier "),
            Gary, CancellationToken.None);

        var sent = Assert.Single(generator.IdeaAdjustments);
        Assert.Equal([beans, bread], sent.Current.Uses);
        Assert.Equal([beans], sent.Changes.Remove.Select(r => r.ProductId!.Value)); // by its generic name; "lobster" isn't used: ignored
        Assert.Equal(("spicier", RecipeMeal.Snack), (sent.Context.Comments, sent.Context.Meal));
        Assert.Equal([cheddar], sent.Context.IncludeProductIds);
        Assert.Equal(3, sent.Context.Stock.Count);
        var adjusted = Assert.IsType<AdjustIdeaResult.Adjusted>(result);
        Assert.Equal(["Heinz Baked Beanz", "Hovis Wholemeal", "Tesco Mature Cheddar"], adjusted.Idea.Uses.Select(u => u.DisplayName));
        Assert.Equal(["Mature cheddar"], adjusted.Summary.Added);
        Assert.Equal(["Heinz Baked Beanz"], adjusted.Summary.Removed);
        Assert.Equal("spicier", adjusted.Summary.Tweak);
    }

    [Theory]
    [InlineData(null)] // no change at all
    [InlineData("tweak")]
    [InlineData("typed")]
    [InlineData("add")]
    public async Task Bad_changes_are_refused_before_any_model_call(string? problem)
    {
        var bread = Known("Hovis Wholemeal");

        var result = await UseCase().ExecuteAsync(problem switch
        {
            "tweak" => Choices([new IdeaUse(bread, null)], tweak: new string('x', 301)),
            "typed" => Choices([new IdeaUse(bread, null)], typed: ["honey", "jam"]), // an idea has room for one extra
            "add" => Choices([new IdeaUse(bread, null)], add: [Guid.NewGuid()]), // not in stock
            _ => Choices([new IdeaUse(bread, null)], remove: ["lobster"]),
        }, Gary, CancellationToken.None);

        Assert.IsType<AdjustIdeaResult.Invalid>(result);
        Assert.Empty(generator.IdeaAdjustments);
    }

    [Fact]
    public async Task What_the_model_couldnt_do_comes_back_by_name()
    {
        var bread = Known("Hovis Wholemeal");
        var cheddar = Known("Tesco Mature Cheddar", generic: "Mature cheddar");
        generator.IdeaAdjusted = r => new IdeaResult.Adjusted(r.Current, [cheddar], ["honey"], ["Hovis Wholemeal"]);

        var result = await UseCase().ExecuteAsync(
            Choices([new IdeaUse(bread, null)], add: [cheddar], typed: ["honey"], remove: ["Hovis Wholemeal"]), Gary, CancellationToken.None);

        var adjusted = Assert.IsType<AdjustIdeaResult.Adjusted>(result);
        Assert.Equal(["Mature cheddar", "honey"], adjusted.CouldNotAdd);
        Assert.Equal(["Hovis Wholemeal"], adjusted.CouldNotRemove);
    }

    [Fact]
    public async Task It_shares_the_hourly_limit_and_says_when_the_model_cant_help()
    {
        var bread = Known("Hovis Wholemeal");
        generator.IdeaAdjusted = _ => new IdeaResult.Unavailable("timeout");

        Assert.IsType<AdjustIdeaResult.Unavailable>(await UseCase().ExecuteAsync(Choices([new IdeaUse(bread, null)], tweak: "a"), Gary, CancellationToken.None));
        Assert.True(rateLimit.TryTake(Gary.ObjectId)); // the limit is 2: one used by the adjustment, one here
        Assert.IsType<AdjustIdeaResult.TooManyRequests>(await UseCase().ExecuteAsync(Choices([new IdeaUse(bread, null)], tweak: "b"), Gary, CancellationToken.None));
        Assert.All(logger.Entries, e => Assert.DoesNotContain("Hovis", e.Message, StringComparison.Ordinal));
    }
}
