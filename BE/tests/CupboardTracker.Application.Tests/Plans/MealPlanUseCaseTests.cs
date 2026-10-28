using System.Text.Json.Nodes;
using CupboardTracker.Application.Plans;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Tests.Plans;

internal sealed class FakePlanRepository : IMealPlanRepository
{
    private readonly Dictionary<Guid, (MealPlan Plan, int Version)> plans = [];

    public int ConflictsToGive { get; set; }

    public MealPlan? Stored(Guid id) => plans.TryGetValue(id, out var p) ? p.Plan : null;

    public Task<Versioned<MealPlan>?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(plans.TryGetValue(id, out var p) ? new Versioned<MealPlan>(p.Plan, $"v{p.Version}") : null);

    public Task<bool> TryCreateAsync(MealPlan plan, CancellationToken cancellationToken)
    {
        if (plans.ContainsKey(plan.Id))
        {
            return Task.FromResult(false);
        }

        plans[plan.Id] = (plan, 1);
        return Task.FromResult(true);
    }

    public Task<bool> TryReplaceAsync(Versioned<MealPlan> current, MealPlan updated, CancellationToken cancellationToken)
    {
        if (!plans.TryGetValue(updated.Id, out var p) || current.ETag != $"v{p.Version}" || ConflictsToGive-- > 0)
        {
            return Task.FromResult(false);
        }

        plans[updated.Id] = (updated, p.Version + 1);
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(plans.Remove(id));
}

internal sealed class FakePlanScheduler : IMealPlanScheduler
{
    public List<Guid> Queued { get; } = [];

    public Task EnqueueAsync(Guid planId, CancellationToken cancellationToken)
    {
        Queued.Add(planId);
        return Task.CompletedTask;
    }
}

internal sealed class FakePlanner : IMealPlanner
{
    public List<OutlineRequest> Outlines { get; } = [];

    public List<ReviseRequest> Revisions { get; } = [];

    public Func<OutlineRequest, PlannerResult>? Outline { get; set; }

    public Func<ReviseRequest, PlannerResult>? Revise { get; set; }

    public Task<PlannerResult> OutlineAsync(OutlineRequest request, CancellationToken cancellationToken)
    {
        Outlines.Add(request);
        return Task.FromResult(Outline?.Invoke(request) ?? new PlannerResult.Unavailable("not set"));
    }

    public Task<PlannerResult> ReviseAsync(ReviseRequest request, CancellationToken cancellationToken)
    {
        Revisions.Add(request);
        return Task.FromResult(Revise?.Invoke(request) ?? new PlannerResult.Unavailable("not set"));
    }
}

internal sealed class FakeHistory : IPurchaseHistory
{
    public IReadOnlyList<PurchaseUnit> Units { get; set; } = [];

    public IReadOnlyList<ListMemoryEntry> Memory { get; set; } = [];

    public Exception? Fail { get; set; }

    public Task<(IReadOnlyList<PurchaseUnit> Units, IReadOnlyList<ListMemoryEntry> Memory)> GetAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        Fail is not null ? throw Fail : Task.FromResult((Units, Memory));
}

// Plan meals (#119): creating a plan (async), writing its meals, and changing it, with the model faked.
public sealed class MealPlanUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 28);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FakeStockQueries queries = new();
    private readonly FakeProducts products = new();
    private readonly FakePlanRepository repository = new();
    private readonly FakePlanScheduler scheduler = new();
    private readonly FakePlanner planner = new();
    private readonly FakeHistory history = new();
    private readonly FixedClock clock = new(Now);
    private readonly ListLogger<MealPlans> logger = new();
    private PlanRateLimit limit;

    public MealPlanUseCaseTests() => limit = new PlanRateLimit(new PlanSettings(), clock);

    private MealPlans UseCase() => new(
        repository, scheduler, planner, new GetStock(queries, products, TestSettings.Stock(), clock), history, UsuallyBuysSettings.Default, limit, clock, logger);

    private Guid Known(string name, int count = 1, int? expiresInDays = null, string? aisle = null, AnimalContent? animal = null)
    {
        var id = Guid.NewGuid();
        var product = Product.Found(id, "5000354922831", new ProductDetails(name, null, null, null, []), Now, 1);
        if (aisle is not null)
        {
            product = product.WithManualAisle(aisle, Now);
        }

        if (animal is { } a)
        {
            product = product.WithManualAnimalContent(a, Now);
        }

        products.Products[id] = product;
        for (var i = 0; i < count; i++)
        {
            queries.Units.Add(new StockUnit(id, null, Now.AddHours(-1), expiresInDays is { } d ? Today.AddDays(d) : null));
        }

        return id;
    }

    private static PlanChoices Choices(
        int days = 2, string[]? meals = null, string diet = "any", bool preferUseFirst = false, string? comments = null, string[]? kit = null) =>
        new(days, meals ?? ["dinner"], diet, kit, preferUseFirst, comments);

    private static DraftMeal Draft(string id, string type, string title, Guid[]? uses = null, string[]? toBuy = null, Guid[]? usesUp = null) =>
        new(id, type, title, $"{title}, simply.", 2, uses ?? [], usesUp ?? [], toBuy ?? []);

    private async Task<Guid> ReadyPlan(PlanChoices choices, params DraftMeal[] drafts)
    {
        planner.Outline = _ => new PlannerResult.Drafted(drafts);
        planner.Revise = _ => new PlannerResult.Unavailable("no retry");
        var created = Assert.IsType<PlanOutcome.Done>(await UseCase().CreateAsync(choices, Gary, TestContext.Current.CancellationToken));
        await UseCase().GenerateAsync(created.View.Plan.Id, TestContext.Current.CancellationToken);
        return created.View.Plan.Id;
    }

    private MealPlan Stored(Guid id) => repository.Stored(id)!;

    // ---- Create

    [Fact]
    public async Task Creating_queues_a_plan_and_returns_it_still_being_made()
    {
        Known("Milk");

        var done = Assert.IsType<PlanOutcome.Done>(await UseCase().CreateAsync(Choices(comments: "  quick meals  "), Gary, TestContext.Current.CancellationToken));

        var plan = done.View.Plan;
        Assert.Equal(PlanStatuses.Generating, plan.Status);
        Assert.Empty(plan.Meals);
        Assert.Equal("quick meals", plan.Choices.Comments);
        Assert.Equal((Gary.ObjectId, "Gary", Now), (plan.CreatedBy, plan.CreatedByName, plan.CreatedAt));
        Assert.Equal([plan.Id], scheduler.Queued);
        Assert.Equal(PlanStatuses.Generating, Stored(plan.Id).Status);
        Assert.Empty(planner.Outlines); // nothing is asked of the model in the request itself
    }

    [Theory]
    [InlineData(0, "dinner", "any", "Plan 1 to 7 days.")]
    [InlineData(8, "dinner", "any", "Plan 1 to 7 days.")]
    [InlineData(3, "", "any", "Pick at least one of breakfast, lunch and dinner.")]
    [InlineData(3, "supper", "any", "Pick at least one of breakfast, lunch and dinner.")]
    [InlineData(3, "dinner", "vegan", "Diet must be any, veg or half.")]
    public async Task Bad_choices_are_refused_before_anything_is_queued(int days, string meals, string diet, string error)
    {
        var choices = Choices(days, meals.Length == 0 ? [] : [meals], diet);

        Assert.Equal(error, Assert.IsType<PlanOutcome.Invalid>(await UseCase().CreateAsync(choices, Gary, TestContext.Current.CancellationToken)).Error);
        Assert.Empty(scheduler.Queued);
    }

    [Fact]
    public async Task A_meal_type_twice_an_unknown_kit_and_long_comments_are_refused()
    {
        var plans = UseCase();

        Assert.IsType<PlanOutcome.Invalid>(await plans.CreateAsync(Choices(meals: ["dinner", "dinner"]), Gary, TestContext.Current.CancellationToken));
        Assert.IsType<PlanOutcome.Invalid>(await plans.CreateAsync(Choices(kit: ["hob", "laser"]), Gary, TestContext.Current.CancellationToken));
        Assert.IsType<PlanOutcome.Invalid>(await plans.CreateAsync(Choices(comments: new string('x', 301)), Gary, TestContext.Current.CancellationToken));
        Assert.Empty(scheduler.Queued);
    }

    [Fact]
    public async Task Plan_calls_have_their_own_hourly_limit_per_person()
    {
        Known("Milk");
        limit = new PlanRateLimit(new PlanSettings(2), clock);
        var plans = UseCase();

        Assert.IsType<PlanOutcome.Done>(await plans.CreateAsync(Choices(), Gary, TestContext.Current.CancellationToken));
        Assert.IsType<PlanOutcome.Done>(await plans.CreateAsync(Choices(), Gary, TestContext.Current.CancellationToken));
        Assert.IsType<PlanOutcome.TooManyRequests>(await plans.CreateAsync(Choices(), Gary, TestContext.Current.CancellationToken));
        Assert.IsType<PlanOutcome.Done>(await plans.CreateAsync(Choices(), new CurrentUser(Guid.NewGuid(), "Sam"), TestContext.Current.CancellationToken));
        clock.Now = Now.AddHours(1).AddSeconds(1);
        Assert.IsType<PlanOutcome.Done>(await plans.CreateAsync(Choices(), Gary, TestContext.Current.CancellationToken));
        Assert.Equal(4, scheduler.Queued.Count); // the refused one queued nothing
    }

    // ---- Generate

    [Fact]
    public async Task The_meals_are_written_with_ids_by_code_in_the_counts_asked_for_and_refs_turned_into_products()
    {
        var beans = Known("Baked beans");
        var rice = Known("Basmati rice", aisle: "pastaRiceGrains");

        var id = await ReadyPlan(
            Choices(days: 2, meals: ["lunch", "dinner"]),
            Draft("x", "dinner", "Bean chilli", [beans, rice], ["peppers"]),
            Draft("y", "lunch", "Rice bowl", [rice]),
            Draft("z", "dinner", "Bean stew", [beans], ["peppers"]),
            Draft("w", "dinner", "A third dinner nobody asked for"),
            Draft("v", "lunch", "Beans on rice", [beans, rice]));

        var plan = Stored(id);
        Assert.Equal(PlanStatuses.Ready, plan.Status);
        Assert.Equal(["lunch-1", "lunch-2", "dinner-1", "dinner-2"], plan.Meals.Select(m => m.Id)); // plan order, lunch first
        Assert.Equal(["Rice bowl", "Beans on rice", "Bean chilli", "Bean stew"], plan.Meals.Select(m => m.Title));
        Assert.Equal(["Baked beans", "Basmati rice"], plan.Meals[2].Uses.Select(u => u.Name));
        Assert.Equal(2, planner.Outlines.Single().Counts["lunch"]);
        Assert.Equal(2, planner.Outlines.Single().Counts["dinner"]);
    }

    [Fact]
    public async Task What_the_model_is_shown_is_the_stock_read_on_the_server_never_the_apps()
    {
        Known("Milk", count: 3);
        Known("Eggs", expiresInDays: 1);
        history.Units = [new PurchaseUnit("semi-skimmed milk", Now.AddDays(-1), null), new PurchaseUnit("semi-skimmed milk", Now.AddDays(-3), null)];

        await ReadyPlan(Choices(), Draft("a", "dinner", "Omelette"), Draft("b", "dinner", "Milk pudding"));

        var context = planner.Outlines.Single().Context;
        Assert.Equal(["Eggs", "Milk"], context.Stock.Select(s => s.Name).Order());
        Assert.Equal(3, context.Stock.Single(s => s.Name == "Milk").Quantity);
        Assert.Equal(["semi-skimmed milk"], context.UsuallyBuys);
    }

    [Fact]
    public async Task Prefer_use_first_off_shows_no_use_first_and_keeps_none_of_the_models_usesUp()
    {
        var milk = Known("Milk", expiresInDays: 1);

        var off = await ReadyPlan(Choices(preferUseFirst: false), Draft("a", "dinner", "Milk pudding", [milk], usesUp: [milk]), Draft("b", "dinner", "Rice"));
        var on = await ReadyPlan(Choices(preferUseFirst: true), Draft("a", "dinner", "Milk pudding", [milk], usesUp: [milk]), Draft("b", "dinner", "Rice"));

        Assert.False(planner.Outlines[0].Context.Choices.PreferUseFirst);
        Assert.True(planner.Outlines[1].Context.Choices.PreferUseFirst);
        Assert.Empty(Stored(off).Meals[0].UsesUp);
        Assert.Equal(["Milk"], Stored(on).Meals[0].UsesUp.Select(u => u.Name));
        var offView = MealPlans.ToView(Stored(off));
        Assert.Empty(offView.Meals[0].UseFirst); // no "Use first" chip when it's off
        Assert.Equal(["Milk"], MealPlans.ToView(Stored(on)).Meals[0].UseFirst);
    }

    [Fact]
    public async Task Only_a_product_that_really_is_use_first_counts_as_used_up()
    {
        var milk = Known("Milk", expiresInDays: 1);
        var tins = Known("Tinned tomatoes", expiresInDays: 300);

        var id = await ReadyPlan(Choices(days: 1, preferUseFirst: true), Draft("a", "dinner", "Pasta", [milk, tins], usesUp: [milk, tins]));

        Assert.Equal(["Milk"], Stored(id).Meals[0].UsesUp.Select(u => u.Name));
    }

    [Fact]
    public async Task Things_to_buy_leave_out_whats_in_stock_repeats_and_more_than_four()
    {
        Known("Onions");

        var id = await ReadyPlan(Choices(days: 1), Draft("a", "dinner", "Stew", toBuy: ["onions", "Peppers", "peppers", "kale", "leeks", "beef", "stock"]));

        Assert.Equal(["Peppers", "kale", "leeks", "beef"], Stored(id).Meals[0].ToBuy);
    }

    [Fact]
    public async Task Things_bought_for_one_meal_get_one_retry_that_names_them_and_a_better_plan_is_kept()
    {
        var beans = Known("Baked beans");
        planner.Revise = request =>
        {
            Assert.Contains("kale", request.Instruction, StringComparison.Ordinal);
            Assert.Contains("leeks", request.Instruction, StringComparison.Ordinal);
            return new PlannerResult.Drafted([Draft("dinner-2", "dinner", "Bean and kale stew", [beans], ["kale"])]);
        };
        planner.Outline = _ => new PlannerResult.Drafted([Draft("a", "dinner", "Kale hash", [beans], ["kale"]), Draft("b", "dinner", "Leek soup", [], ["leeks"])]);
        var created = Assert.IsType<PlanOutcome.Done>(await UseCase().CreateAsync(Choices(), Gary, TestContext.Current.CancellationToken));

        await UseCase().GenerateAsync(created.View.Plan.Id, TestContext.Current.CancellationToken);

        var plan = Stored(created.View.Plan.Id);
        Assert.Equal(PlanStatuses.Ready, plan.Status);
        Assert.Single(planner.Revisions);
        Assert.Empty(PlanOverlap.Orphans(plan.Meals));
        Assert.Equal(["Kale hash", "Bean and kale stew"], plan.Meals.Select(m => m.Title));
    }

    [Fact]
    public async Task A_failed_retry_leaves_the_plan_as_the_outline_made_it()
    {
        Known("Milk");
        planner.Revise = _ => new PlannerResult.Unavailable("timeout");
        planner.Outline = _ => new PlannerResult.Drafted([Draft("a", "dinner", "Kale hash", toBuy: ["kale"]), Draft("b", "dinner", "Leek soup", toBuy: ["leeks"])]);
        var created = Assert.IsType<PlanOutcome.Done>(await UseCase().CreateAsync(Choices(), Gary, TestContext.Current.CancellationToken));

        await UseCase().GenerateAsync(created.View.Plan.Id, TestContext.Current.CancellationToken);

        Assert.Equal(PlanStatuses.Ready, Stored(created.View.Plan.Id).Status);
        Assert.Equal(2, Stored(created.View.Plan.Id).Meals.Count);
    }

    [Fact]
    public async Task Without_enough_history_the_plan_is_made_without_usually_buys_and_a_failing_history_never_fails_it()
    {
        Known("Milk");
        history.Fail = new InvalidOperationException("cosmos is down");

        var id = await ReadyPlan(Choices(days: 1), Draft("a", "dinner", "Milk pudding"));

        Assert.Equal(PlanStatuses.Ready, Stored(id).Status);
        Assert.Empty(planner.Outlines.Single().Context.UsuallyBuys);
        Assert.Contains(logger.At(Microsoft.Extensions.Logging.LogLevel.Warning), m => m.Contains("planning without it", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "Couldn't plan just now. Try again.")]
    [InlineData(true, "Busy right now, try again in a minute.")]
    public async Task A_model_that_cant_plan_leaves_a_failed_plan_with_a_friendly_reason(bool busy, string text)
    {
        Known("Milk");
        planner.Outline = _ => busy ? new PlannerResult.Busy() : new PlannerResult.Unavailable("timeout");
        var created = Assert.IsType<PlanOutcome.Done>(await UseCase().CreateAsync(Choices(), Gary, TestContext.Current.CancellationToken));

        await UseCase().GenerateAsync(created.View.Plan.Id, TestContext.Current.CancellationToken);

        Assert.Equal((PlanStatuses.Failed, text), (Stored(created.View.Plan.Id).Status, Stored(created.View.Plan.Id).Error));
    }

    [Fact]
    public async Task Nothing_in_stock_fails_the_plan_and_a_message_delivered_twice_runs_once()
    {
        var created = Assert.IsType<PlanOutcome.Done>(await UseCase().CreateAsync(Choices(), Gary, TestContext.Current.CancellationToken));
        await UseCase().GenerateAsync(created.View.Plan.Id, TestContext.Current.CancellationToken);
        await UseCase().GenerateAsync(created.View.Plan.Id, TestContext.Current.CancellationToken);

        Assert.Equal(PlanStatuses.Failed, Stored(created.View.Plan.Id).Status);
        Assert.Contains("nothing in stock", Stored(created.View.Plan.Id).Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(planner.Outlines);
        await UseCase().GenerateAsync(Guid.NewGuid(), TestContext.Current.CancellationToken); // an unknown plan is ignored
    }

    [Fact]
    public async Task A_veggie_plan_warns_about_meat_and_the_kit_that_wasnt_ticked_is_flagged()
    {
        var mince = Known("Beef mince", aisle: "meatFish", animal: AnimalContent.Meat);

        var id = await ReadyPlan(
            Choices(days: 2, diet: "veg", kit: ["hob"]),
            Draft("a", "dinner", "Chilli", [mince]),
            Draft("b", "dinner", "Roast veg", toBuy: ["carrots"]) with { Summary = "Roast everything in the oven." });

        var meals = Stored(id).Meals;
        Assert.Contains("Uses Beef mince, which is meat or fish.", meals[0].Warnings);
        Assert.Contains(meals[1].Warnings, w => w.StartsWith("This meal mentions the oven or grill", StringComparison.Ordinal));
        Assert.Equal("veg", Assert.Single(planner.Outlines).Context.Choices.Diet);
    }

    // ---- Read and delete a plan

    [Fact]
    public async Task A_plan_can_be_read_and_deleting_it_twice_is_fine()
    {
        Known("Milk");
        var id = await ReadyPlan(Choices(days: 1), Draft("a", "dinner", "Milk pudding"));

        Assert.NotNull(await UseCase().GetAsync(id, TestContext.Current.CancellationToken));
        await UseCase().DeletePlanAsync(id, TestContext.Current.CancellationToken);
        await UseCase().DeletePlanAsync(id, TestContext.Current.CancellationToken);

        Assert.Null(await UseCase().GetAsync(id, TestContext.Current.CancellationToken));
        Assert.Null(repository.Stored(id));
    }

    // ---- Changes

    private async Task<(Guid Id, Guid Beans, Guid Kale)> ThreeDinners()
    {
        var beans = Known("Baked beans");
        var kale = Known("Curly kale");
        planner.Revise = _ => new PlannerResult.Unavailable("not set");
        var id = await ReadyPlan(Choices(days: 3),
            Draft("a", "dinner", "Bean chilli", [beans], ["peppers", "spinach"]),
            Draft("b", "dinner", "Pepper pasta", [kale], ["peppers"]),
            Draft("c", "dinner", "Spinach curry", [], ["spinach"]));
        planner.Outlines.Clear();
        return (id, beans, kale);
    }

    [Fact]
    public async Task Adjust_a_meal_sends_the_plan_with_everything_else_kept_and_the_wish_in_the_comments()
    {
        var (id, beans, _) = await ThreeDinners();
        planner.Revise = _ => new PlannerResult.Drafted([Draft("dinner-1", "dinner", "Vegetarian bean chilli", [beans], ["peppers", "spinach"])]);

        var done = Assert.IsType<PlanOutcome.Done>(await UseCase().AdjustMealAsync(id, "dinner-1", "  make it vegetarian  ", Gary, TestContext.Current.CancellationToken));

        var request = Assert.Single(planner.Revisions);
        Assert.Equal("make it vegetarian", request.Comments);
        Assert.Equal([false, true, true], request.Plan.Select(p => p.Keep));
        Assert.Contains("dinner-1", request.Instruction, StringComparison.Ordinal);
        Assert.Equal("Vegetarian bean chilli", Stored(id).Meals[0].Title);
        Assert.Equal(["Pepper pasta", "Spinach curry"], Stored(id).Meals.Skip(1).Select(m => m.Title));
        Assert.Equal(3, done.View.Meals.Count);
    }

    [Fact]
    public async Task Adjusting_clears_the_meals_recipe_because_it_changed_and_never_touches_the_others()
    {
        var (id, _, _) = await ThreeDinners();
        var plan = Stored(id);
        var recipe = new JsonObject { ["title"] = "Written already" };
        var withRecipes = plan with { Meals = [.. plan.Meals.Select(m => m with { Recipe = recipe.DeepClone().AsObject() })] };
        Assert.True(await repository.TryReplaceAsync((await repository.GetAsync(id, TestContext.Current.CancellationToken))!, withRecipes, TestContext.Current.CancellationToken));
        planner.Revise = _ => new PlannerResult.Drafted([Draft("dinner-2", "dinner", "Spicier pepper pasta", [], ["peppers"])]);

        await UseCase().AdjustMealAsync(id, "dinner-2", "spicier", Gary, TestContext.Current.CancellationToken);

        var meals = Stored(id).Meals;
        Assert.Null(meals[1].Recipe);
        Assert.NotNull(meals[0].Recipe);
        Assert.NotNull(meals[2].Recipe);
    }

    [Fact]
    public async Task Another_one_tells_the_model_the_angle_the_titles_to_avoid_and_what_would_be_stranded()
    {
        var (id, _, _) = await ThreeDinners();
        planner.Revise = _ => new PlannerResult.Drafted([Draft("dinner-3", "dinner", "Spinach and pepper rice", [], ["peppers", "spinach"])]);

        var done = Assert.IsType<PlanOutcome.Done>(await UseCase().AnotherAsync(id, "dinner-3", Gary, TestContext.Current.CancellationToken));

        var instruction = Assert.Single(planner.Revisions).Instruction;
        Assert.Contains(PlanAngles.All, angle => instruction.Contains(angle, StringComparison.Ordinal));
        Assert.Contains("Avoid these titles: Bean chilli; Pepper pasta; Spinach curry.", instruction, StringComparison.Ordinal);
        Assert.Contains("The other meals buy spinach in sharing with it", instruction, StringComparison.Ordinal);
        Assert.Equal("Spinach and pepper rice", Stored(id).Meals[2].Title);
        Assert.Equal("dinner-3", done.View.Meals[2].Meal.Id);
    }

    [Fact]
    public async Task Add_a_meal_adds_one_of_that_type_with_a_new_id_and_favours_overlap_with_the_rest()
    {
        var (id, _, _) = await ThreeDinners();
        planner.Revise = _ => new PlannerResult.Drafted([Draft("whatever", "lunch", "Pepper soup", [], ["peppers"])]);

        var done = Assert.IsType<PlanOutcome.Done>(await UseCase().AddMealAsync(id, "lunch", Gary, TestContext.Current.CancellationToken));

        var request = Assert.Single(planner.Revisions);
        Assert.Equal(1, request.Counts["lunch"]);
        Assert.Equal(3, request.Counts["dinner"]);
        Assert.All(request.Plan, p => Assert.True(p.Keep)); // the rest of the plan is shown, all kept
        Assert.Equal(["dinner-1", "dinner-2", "dinner-3", "lunch-1"], Stored(id).Meals.Select(m => m.Id));
        Assert.Equal(2, done.View.Meals[3].Shares!.OtherMeals);
        Assert.IsType<PlanOutcome.NotFound>(await UseCase().AddMealAsync(id, "supper", Gary, TestContext.Current.CancellationToken)); // the function refuses the type first
    }

    [Fact]
    public async Task Adjusting_the_whole_plan_keeps_the_kept_meals_exactly_and_their_recipes()
    {
        var (id, beans, _) = await ThreeDinners();
        var plan = Stored(id);
        var recipe = new JsonObject { ["title"] = "Written already" };
        var marked = plan with { Meals = [.. plan.Meals.Select(m => m.Id == "dinner-1" ? m with { Keep = true, Recipe = recipe } : m)] };
        Assert.True(await repository.TryReplaceAsync((await repository.GetAsync(id, TestContext.Current.CancellationToken))!, marked, TestContext.Current.CancellationToken));
        planner.Revise = _ => new PlannerResult.Drafted(
        [
            Draft("dinner-1", "dinner", "The model tried to change a kept meal", [beans]),
            Draft("dinner-2", "dinner", "Cheaper pasta", [], ["peppers"]),
            Draft("dinner-3", "dinner", "Cheaper curry", [], ["peppers"]),
        ]);

        await UseCase().AdjustPlanAsync(id, "cheaper", Gary, TestContext.Current.CancellationToken);

        var request = Assert.Single(planner.Revisions);
        Assert.Equal([true, false, false], request.Plan.Select(p => p.Keep));
        var meals = Stored(id).Meals;
        Assert.Equal(["Bean chilli", "Cheaper pasta", "Cheaper curry"], meals.Select(m => m.Title));
        Assert.Equal("Written already", (string?)meals[0].Recipe!["title"]);
        Assert.True(meals[0].Keep);
    }

    [Fact]
    public async Task Keep_toggles_and_a_missing_meal_is_not_found()
    {
        var (id, _, _) = await ThreeDinners();

        Assert.IsType<PlanOutcome.Done>(await UseCase().KeepAsync(id, "dinner-2", true, TestContext.Current.CancellationToken));
        Assert.True(Stored(id).Meals[1].Keep);
        Assert.IsType<PlanOutcome.Done>(await UseCase().KeepAsync(id, "dinner-2", false, TestContext.Current.CancellationToken));
        Assert.False(Stored(id).Meals[1].Keep);
        Assert.IsType<PlanOutcome.NotFound>(await UseCase().KeepAsync(id, "dinner-9", true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_a_meal_returns_it_for_undo_and_undo_puts_it_back_once()
    {
        var (id, _, _) = await ThreeDinners();

        var deleted = Assert.IsType<PlanOutcome.MealDeleted>(await UseCase().DeleteMealAsync(id, "dinner-2", TestContext.Current.CancellationToken));
        Assert.Equal(["dinner-1", "dinner-3"], Stored(id).Meals.Select(m => m.Id));
        Assert.Equal("Pepper pasta", deleted.Meal.Title);
        Assert.IsType<PlanOutcome.NotFound>(await UseCase().DeleteMealAsync(id, "dinner-2", TestContext.Current.CancellationToken));

        Assert.IsType<PlanOutcome.Done>(await UseCase().RestoreMealAsync(id, deleted.Meal, TestContext.Current.CancellationToken));
        Assert.IsType<PlanOutcome.Done>(await UseCase().RestoreMealAsync(id, deleted.Meal, TestContext.Current.CancellationToken));
        Assert.Equal(3, Stored(id).Meals.Count);
        Assert.Equal(1, Stored(id).Meals.Count(m => m.Id == "dinner-2"));
        Assert.IsType<PlanOutcome.Invalid>(await UseCase().RestoreMealAsync(id, deleted.Meal with { Type = "supper" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failed_call_leaves_the_plan_exactly_as_it_was()
    {
        var (id, _, _) = await ThreeDinners();
        var before = Stored(id);
        planner.Revise = _ => new PlannerResult.Unavailable("timeout");

        Assert.IsType<PlanOutcome.Unavailable>(await UseCase().AnotherAsync(id, "dinner-1", Gary, TestContext.Current.CancellationToken));
        planner.Revise = _ => new PlannerResult.Busy();
        Assert.IsType<PlanOutcome.Busy>(await UseCase().AdjustMealAsync(id, "dinner-1", "spicier", Gary, TestContext.Current.CancellationToken));
        planner.Revise = _ => new PlannerResult.Drafted([]);
        Assert.IsType<PlanOutcome.Unavailable>(await UseCase().AdjustPlanAsync(id, "cheaper", Gary, TestContext.Current.CancellationToken));

        Assert.Same(before, Stored(id));
    }

    [Fact]
    public async Task Someone_changing_the_plan_meanwhile_is_a_conflict_not_a_lost_update()
    {
        var (id, beans, _) = await ThreeDinners();
        planner.Revise = _ => new PlannerResult.Drafted([Draft("dinner-1", "dinner", "Changed", [beans])]);
        repository.ConflictsToGive = 1;

        Assert.IsType<PlanOutcome.Conflict>(await UseCase().AdjustMealAsync(id, "dinner-1", "spicier", Gary, TestContext.Current.CancellationToken));
        Assert.Equal("Bean chilli", Stored(id).Meals[0].Title);
    }

    [Fact]
    public async Task Changes_need_a_ready_plan_a_wish_to_act_on_and_count_towards_the_limit()
    {
        var (id, _, _) = await ThreeDinners();
        var generating = Assert.IsType<PlanOutcome.Done>(await UseCase().CreateAsync(Choices(), Gary, TestContext.Current.CancellationToken));

        Assert.IsType<PlanOutcome.NotReady>(await UseCase().AnotherAsync(generating.View.Plan.Id, "dinner-1", Gary, TestContext.Current.CancellationToken));
        Assert.IsType<PlanOutcome.NotFound>(await UseCase().AnotherAsync(Guid.NewGuid(), "dinner-1", Gary, TestContext.Current.CancellationToken));
        Assert.IsType<PlanOutcome.Invalid>(await UseCase().AdjustMealAsync(id, "dinner-1", "  ", Gary, TestContext.Current.CancellationToken));
        Assert.IsType<PlanOutcome.Invalid>(await UseCase().AdjustPlanAsync(id, new string('x', 301), Gary, TestContext.Current.CancellationToken));
        Assert.IsType<PlanOutcome.NotFound>(await UseCase().AdjustMealAsync(id, "dinner-9", "spicier", Gary, TestContext.Current.CancellationToken));

        limit = new PlanRateLimit(new PlanSettings(1), clock);
        planner.Revise = _ => new PlannerResult.Drafted([Draft("dinner-1", "dinner", "Another bean dish")]);
        Assert.IsType<PlanOutcome.Done>(await UseCase().AnotherAsync(id, "dinner-1", Gary, TestContext.Current.CancellationToken));
        Assert.IsType<PlanOutcome.TooManyRequests>(await UseCase().AnotherAsync(id, "dinner-1", Gary, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_cards_facts_come_from_the_whole_plan_not_the_models_words()
    {
        var (id, _, _) = await ThreeDinners();

        var view = MealPlans.ToView(Stored(id));

        Assert.Equal(1, view.Meals[0].UsesCount);
        Assert.Equal(["peppers", "spinach"], view.Meals[0].Shares!.Names);
        Assert.Equal(2, view.Meals[0].Shares!.OtherMeals);
        Assert.Equal(["peppers"], view.Meals[1].Shares!.Names);
        Assert.Equal(1, view.Meals[1].Shares!.OtherMeals);
        Assert.Equal(0, view.Meals[2].UsesCount);
    }

    [Fact]
    public void Plans_round_trip_through_json_with_utc_timestamps_and_recipes_kept_as_given()
    {
        var plan = new MealPlan(Guid.NewGuid(), Gary.ObjectId, "Gary", Now, Now, PlanStatuses.Ready, null, Choices(),
            [new PlannedMeal("dinner-1", "dinner", "Chilli", "A chilli.", 2, [new PlannedProduct(Guid.NewGuid(), "Beans")], [], ["peppers"], true, ["w"], new JsonObject { ["x"] = 1 })],
            Now);

        var json = MealPlanJson.ToNode(plan);
        var back = MealPlanJson.FromNode(json)!;

        Assert.Equal("2026-10-28T18:00:00.000Z", (string?)json["createdAt"]);
        Assert.Equal("ready", (string?)json["status"]);
        Assert.Equal(plan.Meals[0].Recipe!.ToJsonString(), back.Meals[0].Recipe!.ToJsonString());
        Assert.Equal((plan.Id, plan.Choices.Days, back.AddedToListAt), (back.Id, back.Choices.Days, Now));
        Assert.Equal(plan.Meals[0].ToBuy, back.Meals[0].ToBuy);
    }

    [Fact]
    public async Task No_prompt_stock_comments_titles_or_items_are_logged()
    {
        Known("Secret saffron");
        await ReadyPlan(Choices(days: 1, comments: "private wish"), Draft("a", "dinner", "Hidden dish", toBuy: ["hidden item"]));

        var all = string.Join('\n', logger.Entries.Select(e => e.Message));
        Assert.NotEmpty(all);
        foreach (var secret in new[] { "saffron", "private wish", "Hidden dish", "hidden item" })
        {
            Assert.DoesNotContain(secret, all, StringComparison.OrdinalIgnoreCase);
        }
    }
}
