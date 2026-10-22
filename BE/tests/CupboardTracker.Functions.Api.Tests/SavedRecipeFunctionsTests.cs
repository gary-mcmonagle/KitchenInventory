using System.Text.Json;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.SavedRecipes;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Recipes;

namespace CupboardTracker.Functions.Api.Tests;

// Saved recipes (#105).
public sealed class SavedRecipeFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("List")]
    [InlineData("Get")]
    [InlineData("Save")]
    [InlineData("Update")]
    [InlineData("Delete")]
    public void All_need_a_signed_in_user(string method) =>
        Assert.True(FunctionAuthPolicy.RequiresUser($"CupboardTracker.Functions.Api.Recipes.SavedRecipeFunctions.{method}"));

    [Fact]
    public void A_recipe_body_parses()
    {
        using var body = JsonDocument.Parse("""
            {"kind":"recipe","title":"Penne","meal":"dinner","totalMinutes":25,"servings":2,
             "ingredients":[{"name":"Penne","amount":"200 g","productId":"22222222-2222-2222-2222-222222222222"},{"name":"Salt"}],
             "steps":["Cook it."],"alsoNeeded":["1 onion"]}
            """);

        var (save, error) = SavedRecipeFunctions.ParseSave(Guid.NewGuid(), body);

        Assert.Null(error);
        Assert.Equal(("recipe", RecipeMeal.Dinner, 25, 2), (save!.Kind, save.Meal, save.TotalMinutes, save.Servings));
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), save.Ingredients[0].ProductId);
        Assert.Null(save.Ingredients[1].Amount);
    }

    [Fact]
    public void A_ready_now_body_uses_uses()
    {
        using var body = JsonDocument.Parse("""
            {"kind":"readyNow","title":"Beans on toast","meal":"snack","totalMinutes":5,"uses":[{"name":"Baked beans"}],"how":"Microwave.","appliance":"microwave"}
            """);

        var (save, _) = SavedRecipeFunctions.ParseSave(Guid.NewGuid(), body);

        Assert.Equal(("Baked beans", "microwave"), (save!.Ingredients[0].Name, save.Appliance));
    }

    [Theory]
    [InlineData("""{"kind":"recipe","meal":"dinner","totalMinutes":5,"ingredients":[]}""")]
    [InlineData("""{"kind":"recipe","title":"x","meal":"supper","totalMinutes":5,"ingredients":[]}""")]
    [InlineData("""{"kind":"recipe","title":"x","meal":"dinner","ingredients":[]}""")]
    [InlineData("""{"kind":"recipe","title":"x","meal":"dinner","totalMinutes":5,"ingredients":[{"amount":"1"}]}""")]
    [InlineData("""{"kind":"recipe","title":"x","meal":"dinner","totalMinutes":5,"ingredients":[],"steps":"no"}""")]
    public void Bad_bodies_are_refused(string json)
    {
        using var body = JsonDocument.Parse(json);
        Assert.NotNull(SavedRecipeFunctions.ParseSave(Guid.NewGuid(), body).Error);
    }

    [Fact]
    public void The_response_carries_each_ingredients_status_and_the_summary()
    {
        var recipe = new SavedRecipe(Guid.NewGuid(), "recipe", "Traybake", "Traybake", RecipeMeal.Dinner, 40, 2,
            [new SavedIngredient("Peppers", "2", null, null), new SavedIngredient("Halloumi", "225 g", null, null)],
            [new("Roast.")], [], null, null, "", Guid.NewGuid(), "Sam", DateTimeOffset.UnixEpoch, Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        var view = new SavedRecipeView(recipe, [IngredientStatus.UseFirst, IngredientStatus.Missing], ["Halloumi"], ["Peppers"]);

        var json = JsonSerializer.SerializeToElement(SavedRecipeFunctions.ToJson(view), WebJson);

        Assert.Equal(["useFirst", "missing"], json.GetProperty("ingredients").EnumerateArray().Select(i => i.GetProperty("status").GetString()));
        Assert.Equal("Halloumi", json.GetProperty("missing")[0].GetString());
        Assert.Equal("Sam", json.GetProperty("savedByName").GetString());
    }
}
