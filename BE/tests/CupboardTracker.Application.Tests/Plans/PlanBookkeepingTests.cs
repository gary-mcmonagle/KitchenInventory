using CupboardTracker.Application.Plans;

namespace CupboardTracker.Application.Tests.Plans;

// The code that does a plan's bookkeeping (#119, spike #124): stock allocation, overlap, usually buys.
public sealed class PlanBookkeepingTests
{
    private static readonly Guid Tin = Guid.NewGuid();
    private static readonly Guid Pasta = Guid.NewGuid();
    private static readonly Guid Peppers = Guid.NewGuid();

    private static readonly Dictionary<Guid, PlanStockItem> Stock = new()
    {
        [Tin] = new(Tin, "Chopped tomatoes", "chopped tomatoes", 1, "tinsJars", false),
        [Pasta] = new(Pasta, "Fusilli", "pasta", 1, "pastaRiceGrains", false),
        [Peppers] = new(Peppers, "Peppers", "peppers", 2, "fruitVeg", false),
    };

    private static PlannedMeal Meal(string id, Guid[]? uses = null, string[]? toBuy = null, bool keep = false) =>
        new(id, "dinner", id, "", 2, [.. (uses ?? []).Select(u => new PlannedProduct(u, Stock[u].Name))], [], toBuy ?? [], keep, []);

    // ---- Stock allocation

    [Fact]
    public void One_unit_serves_one_meal_so_a_second_meal_wanting_the_one_tin_is_short()
    {
        var result = StockAllocation.Shortfalls([Meal("m1", [Tin]), Meal("m2", [Tin])], Stock);

        Assert.Equal(["chopped tomatoes"], result["m2"]);
        Assert.False(result.ContainsKey("m1"));
    }

    [Fact]
    public void Bulk_products_serve_every_meal()
    {
        Assert.Empty(StockAllocation.Shortfalls([Meal("m1", [Pasta]), Meal("m2", [Pasta]), Meal("m3", [Pasta])], Stock));
    }

    [Fact]
    public void A_quantity_of_two_serves_two_meals_and_not_a_third()
    {
        var result = StockAllocation.Shortfalls([Meal("m1", [Peppers]), Meal("m2", [Peppers]), Meal("m3", [Peppers])], Stock);

        Assert.Equal(["m3"], result.Keys);
    }

    [Fact]
    public void Kept_meals_claim_first_so_they_never_lose_their_stock_to_a_new_meal()
    {
        var result = StockAllocation.Shortfalls([Meal("m1", [Tin]), Meal("m2", [Tin], keep: true)], Stock);

        Assert.Equal(["m1"], result.Keys);
    }

    [Fact]
    public void Products_not_in_stock_are_ignored()
    {
        Assert.Empty(StockAllocation.Shortfalls([Meal("m1", [Tin])], new Dictionary<Guid, PlanStockItem>()));
    }

    [Theory]
    [InlineData("pastaRiceGrains", "Anything", true)]
    [InlineData("bakingSpices", "Anything", true)]
    [InlineData("fruitVeg", "Basmati rice", true)]
    [InlineData("fruitVeg", "Onions", false)]
    [InlineData("tinsJars", "Kidney beans", false)]
    public void Bulk_is_by_aisle_or_by_a_name_that_says_so(string aisle, string name, bool bulk) =>
        Assert.Equal(bulk, StockAllocation.IsBulk(new PlanStockItem(Guid.NewGuid(), name, name, 1, aisle, false)));

    // ---- Overlap

    [Fact]
    public void Things_bought_for_one_meal_are_orphans_and_for_two_or_more_are_shared()
    {
        var meals = new[]
        {
            Meal("m1", toBuy: ["Peppers", "pak choi"]), Meal("m2", toBuy: ["pepper"]), Meal("m3", toBuy: ["Cabbage"]),
        };

        Assert.Equal(["pak choi", "Cabbage"], PlanOverlap.Orphans(meals).Select(o => o.Name));
        var shares = PlanOverlap.SharesOf(meals, meals[0]);
        Assert.Equal(["Peppers"], shares!.Names);
        Assert.Equal(1, shares.OtherMeals);
        Assert.Null(PlanOverlap.SharesOf(meals, meals[2]));
    }

    [Fact]
    public void Plurals_and_filler_words_dont_hide_sharing()
    {
        Assert.Equal(PlanOverlap.Normalise("Fresh Tomatoes"), PlanOverlap.Normalise("tomato"));
        Assert.Equal(PlanOverlap.Normalise("brown mushrooms"), PlanOverlap.Normalise("Brown mushroom"));
    }

    [Fact]
    public void Replacing_a_meal_would_strand_what_it_shares_with_exactly_one_other_meal()
    {
        var meals = new[]
        {
            Meal("m1", toBuy: ["spinach", "mushrooms"]), Meal("m2", toBuy: ["spinach"]), Meal("m3", toBuy: ["mushrooms", "mushrooms"]),
            Meal("m4", toBuy: ["mushrooms"]),
        };

        // spinach is shared with one other meal (stranded); mushrooms with two others (still shared).
        Assert.Equal(["spinach"], PlanOverlap.Stranded(meals, "m1"));
        Assert.Empty(PlanOverlap.Stranded(meals, "m4"));
    }

    [Fact]
    public void An_empty_plan_has_no_orphans_or_shares()
    {
        Assert.Empty(PlanOverlap.Orphans([]));
        Assert.Empty(PlanOverlap.Needs([]));
    }

    [Fact]
    public void Another_one_picks_an_angle_from_the_list()
    {
        for (var i = 0; i < 20; i++)
        {
            Assert.Contains(PlanAngles.Pick(new Random(i)), PlanAngles.All);
        }

        Assert.True(PlanAngles.All.Count >= 8);
    }

    // ---- Usually buys

    private static readonly DateTimeOffset Start = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
    private static readonly UsuallyBuysSettings Settings = new(Start);

    private static PurchaseUnit Bought(string key, int day, int? usedOnDay = null) =>
        new(key, Start.AddDays(day).AddHours(18), usedOnDay is { } d ? Start.AddDays(d).AddHours(12) : null);

    [Fact]
    public void Two_different_shopping_days_make_a_name_usual_and_one_day_alone_doesnt()
    {
        var units = new[] { Bought("milk", 0), Bought("milk", 4), Bought("bread", 4), Bought("bread", 4) };

        Assert.Equal(["milk"], UsuallyBuys.Rank(units, [], Settings));
    }

    [Fact]
    public void A_use_and_a_list_add_each_count_a_point()
    {
        var units = new[] { Bought("yoghurt", 1, usedOnDay: 3), Bought("eggs", 1) };
        var memory = new[] { new ListMemoryEntry("Eggs", 1, "dairyEggs"), new ListMemoryEntry("Honey", 1, "tinsJars") };

        Assert.Equal(["eggs", "yoghurt"], UsuallyBuys.Rank(units, memory, Settings).Order());
    }

    [Fact]
    public void History_from_before_the_stock_take_ended_doesnt_count()
    {
        var units = new[] { new PurchaseUnit("flour", Start.AddDays(-1), Start.AddDays(-1).AddHours(2)), new PurchaseUnit("flour", Start.AddDays(-2), null) };

        Assert.Empty(UsuallyBuys.Rank(units, [], Settings));
    }

    [Fact]
    public void Non_food_names_from_the_list_are_left_out_and_the_list_is_capped_best_first()
    {
        var memory = new[] { new ListMemoryEntry("Kitchen foil", 3, "other"), new ListMemoryEntry("Milk", 3, "dairyEggs"), new ListMemoryEntry("Tea", 2, "drinks") };

        Assert.Equal(["milk"], UsuallyBuys.Rank([], memory, Settings with { TopN = 1 }));
        Assert.DoesNotContain("kitchen foil", UsuallyBuys.Rank([], memory, Settings));
    }

    [Fact]
    public void Little_or_no_history_gives_an_empty_list_not_an_error()
    {
        Assert.Empty(UsuallyBuys.Rank([], [], Settings));
        Assert.Empty(UsuallyBuys.Rank([Bought("rice", 1)], [], Settings));
    }

    [Fact]
    public void Trips_are_capped_at_four_and_the_floor_is_a_setting()
    {
        var units = Enumerable.Range(0, 9).Select(d => Bought("milk", d)).ToList();

        Assert.Single(UsuallyBuys.Rank(units, [], Settings));
        Assert.Empty(UsuallyBuys.Rank(units, [], Settings with { MinScore = 5 })); // 4 trips at most
    }
}
