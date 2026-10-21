using System.Text.Json.Nodes;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.SavedRecipes;
using CupboardTracker.Infrastructure.Cosmos;

namespace CupboardTracker.Infrastructure.Tests;

// Saved recipes (#105): the savedRecipes documents.
public sealed class SavedRecipeStorageTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_recipe_is_one_document_in_the_household_partition_and_round_trips()
    {
        var recipe = new SavedRecipe(Guid.NewGuid(), SavedRecipeKinds.Recipe, "Sam's traybake", "Halloumi traybake", RecipeMeal.Dinner, 40, 2,
            [new SavedIngredient("Peppers", "2", Guid.NewGuid(), "Peppers"), new SavedIngredient("Salt", null, null, null)],
            ["Heat the oven.", "Roast."], ["225 g halloumi"], null, null, "Halve the chilli", Guid.NewGuid(), "Sam", At, Guid.NewGuid(), At.AddHours(1));

        var document = CosmosSavedRecipeRepository.ToDocument(recipe);

        Assert.Equal(("household", "recipe", "dinner"), (document["householdId"]!.GetValue<string>(), document["kind"]!.GetValue<string>(), document["meal"]!.GetValue<string>()));
        Assert.NotNull(document["ingredients"]);
        Assert.Null(document["uses"]);
        var back = CosmosSavedRecipeRepository.FromDocument(JsonNode.Parse(document.ToJsonString())!.AsObject());
        Assert.Equal(recipe with { Ingredients = [], Steps = [], AlsoNeeded = [] }, back with { Ingredients = [], Steps = [], AlsoNeeded = [] });
        Assert.Equal(recipe.Ingredients, back.Ingredients);
        Assert.Equal(recipe.Steps, back.Steps);
        Assert.Equal(recipe.AlsoNeeded, back.AlsoNeeded);
    }

    [Fact]
    public void A_ready_now_idea_stores_uses_how_and_appliance()
    {
        var idea = new SavedRecipe(Guid.NewGuid(), SavedRecipeKinds.ReadyNow, "Beans on toast", "Beans on toast", RecipeMeal.Snack, 5, null,
            [new SavedIngredient("Baked beans", null, null, "Baked beans")], [], [], "Microwave the beans.", "microwave", "", Guid.NewGuid(), "Gary", At, Guid.NewGuid(), At);

        var document = CosmosSavedRecipeRepository.ToDocument(idea);

        Assert.Equal(("microwave", "Microwave the beans."), (document["appliance"]!.GetValue<string>(), document["how"]!.GetValue<string>()));
        Assert.Null(document["steps"]);
        Assert.Equal("Baked beans", CosmosSavedRecipeRepository.FromDocument(JsonNode.Parse(document.ToJsonString())!.AsObject()).Ingredients[0].Name);
    }
}
