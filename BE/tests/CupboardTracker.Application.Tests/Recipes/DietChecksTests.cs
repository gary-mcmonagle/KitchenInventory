using CupboardTracker.Application.Recipes;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Tests.Recipes;

// Veggie and Half & half (#110): the meat/fish word list and the checks.
public sealed class DietChecksTests
{
    private static readonly Guid Bacon = Guid.NewGuid();
    private static readonly Guid Beans = Guid.NewGuid();
    private static readonly Guid Pesto = Guid.NewGuid();

    private static RecipeRequest Request(Diet diet) => new(RecipeMeal.Dinner, false,
    [
        new(Bacon, "Smoked back bacon", 1, null, false, "Meat & fish", 0, AnimalContent.Meat),
        new(Beans, "Black beans", 1, null, false, "Tins & jars", 0, AnimalContent.None),
        new(Pesto, "Green pesto", 1, null, false, "Sauces & oils", 0, AnimalContent.Unknown),
    ], [], null, [], Diet: diet);

    private static Recipe Recipe(IReadOnlyList<RecipeIngredient> ingredients, IReadOnlyList<RecipeStep> steps, IReadOnlyList<ExtraItem>? also = null,
        int servings = 2, int meat = 0, int veg = 0) =>
        new("Test", "", RecipeMeal.Dinner, servings, 30, ingredients, steps, [], also ?? [], [], ServingsMeat: meat, ServingsVeg: veg);

    [Theory]
    [InlineData("Fry the bacon until crisp.", "bacon")]
    [InlineData("Add a splash of Worcestershire sauce.", "worcestershire")]
    [InlineData("Pour in 500 ml chicken stock.", "chicken stock")]
    [InlineData("Season with fish sauce.", "fish sauce")]
    [InlineData("Brown the mince.", "mince")]
    [InlineData("Add the vegetable stock.", null)] // veggie forms are fine
    [InlineData("Fry the veggie sausages.", null)]
    [InlineData("Brown the meat-free mince.", null)]
    [InlineData("Add 2 vegetarian chicken pieces.", null)]
    [InlineData("Add the minced garlic and chickpeas.", null)] // not meat words
    [InlineData("Grate the halloumi over the shampoo bottle.", null)]
    public void The_word_list_finds_meat_and_fish_but_not_veggie_forms(string text, string? word) => Assert.Equal(word, DietChecks.MeatWordIn(text));

    [Fact]
    public void Nothing_is_checked_for_meat_and_veg() =>
        Assert.True(DietChecks.Check(Recipe([new("Bacon", "4 rashers", Bacon)], [new("Fry the bacon.")]), Request(Diet.Any)).Ok);

    [Fact]
    public void Veggie_refuses_meat_products_and_words_and_warns_about_unknown_ones()
    {
        var check = DietChecks.Check(Recipe(
            [new("Smoked back bacon", "4 rashers", Bacon), new("Black beans", "1 tin", Beans), new("Green pesto", "2 tbsp", Pesto)],
            [new("Simmer the beans in beef stock.")], [new("1 tsp Worcestershire sauce")]), Request(Diet.Veg));

        Assert.Equal(3, check.Problems.Count); // the bacon product, the step's beef stock, the extra Worcestershire
        Assert.Contains(check.Problems, p => p.ForPeople == "Smoked back bacon is meat, which isn't vegetarian.");
        Assert.Contains(check.Problems, p => p.ForPeople == "Worcestershire sauce usually contains anchovies, which isn't vegetarian.");
        Assert.Equal(["Check Green pesto is vegetarian."], check.Warnings);
    }

    [Fact]
    public void A_clean_veggie_recipe_passes()
    {
        var check = DietChecks.Check(Recipe([new("Black beans", "1 tin", Beans)], [new("Simmer the beans in vegetable stock.")]), Request(Diet.Veg));
        Assert.True(check.Ok);
        Assert.Empty(check.Warnings);
    }

    [Fact]
    public void Half_and_half_keeps_meat_on_the_meat_plates_and_both_sides_present_with_the_servings_adding_up()
    {
        var good = Recipe(
            [new("Black beans", "1 tin", Beans, PlateFor.Veg), new("Smoked back bacon", "4 rashers", Bacon, PlateFor.Meat), new("Rice", "150 g", null)],
            [new("Cook the rice."), new("Fry the bacon.", PlateFor.Meat), new("Warm the beans.", PlateFor.Veg)], meat: 1, veg: 1);
        Assert.True(DietChecks.Check(good, Request(Diet.Half)).Ok);

        var bad = good with
        {
            Ingredients = [new("Smoked back bacon", "4 rashers", Bacon), new("Rice", "150 g", null)], // bacon for everyone
            Steps = [new("Fry the bacon and stir it through the rice.")],
            AlsoNeeded = [new("1 tsp Worcestershire sauce", PlateFor.Meat)], // fine: meat side
            ServingsMeat = 2,
            ServingsVeg = 1,
        };
        var check = DietChecks.Check(bad, Request(Diet.Half));
        Assert.Contains(check.Problems, p => p.ForPeople == "Smoked back bacon is meat: keep it to the meat plates.");
        Assert.Contains(check.Problems, p => p.ForPeople == "This recipe mentions bacon: keep it to the meat plates.");
        Assert.Contains(check.Problems, p => p.ForPeople == "This doesn't clearly split into meat and veggie plates.");
        Assert.Contains(check.Problems, p => p.ForPeople == "Check how many plates are meat and how many veggie.");
        Assert.DoesNotContain(check.Problems, p => p.ForPeople.Contains("Worcestershire", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_products_on_the_shared_side_of_half_and_half_are_a_warning()
    {
        var recipe = Recipe(
            [new("Green pesto", "2 tbsp", Pesto), new("Smoked back bacon", "4 rashers", Bacon, PlateFor.Meat)],
            [new("Stir in the pesto."), new("Top with bacon.", PlateFor.Meat)], meat: 1, veg: 1);
        Assert.Equal(["Check Green pesto is vegetarian."], DietChecks.Check(recipe, Request(Diet.Half)).Warnings);
    }

    [Fact]
    public void A_veggie_idea_is_checked_by_its_uses_and_words()
    {
        var bad = new QuickIdea("Bacon butty", "Grill the bacon.", Appliance.Toaster, 8, [Bacon], null);
        var good = new QuickIdea("Beans on toast", "Warm the beans.", Appliance.Microwave, 5, [Beans, Pesto], null);

        Assert.False(DietChecks.Check(bad, Request(Diet.Veg)).Ok);
        Assert.True(DietChecks.Check(good, Request(Diet.Veg)).Ok);
        Assert.Equal(["Check Green pesto is vegetarian."], DietChecks.Check(good, Request(Diet.Veg)).Warnings);
        Assert.True(DietChecks.Check(bad, Request(Diet.Any)).Ok);
    }
}
