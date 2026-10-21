using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Recipes;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class RecipeFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Needs_a_signed_in_user() =>
        Assert.True(FunctionAuthPolicy.RequiresUser("CupboardTracker.Functions.Api.Recipes.RecipeFunctions.Suggest"));

    [Fact]
    public void A_full_body_is_read()
    {
        var id = Guid.NewGuid();
        var (choices, error) = RecipeFunctions.Parse(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"meal":"lunch","preferUseFirst":true,"includeProductIds":["{{id}}"],"comments":"no oven","avoidTitles":["Omelette"]}"""));

        Assert.Null(error);
        Assert.Equal(RecipeMeal.Lunch, choices!.Meal);
        Assert.True(choices.PreferUseFirst);
        Assert.Equal([id], choices.IncludeProductIds);
        Assert.Equal("no oven", choices.Comments);
        Assert.Equal(["Omelette"], choices.AvoidTitles);
    }

    [Fact]
    public void Only_the_meal_is_required()
    {
        var (choices, _) = RecipeFunctions.Parse("""{"meal":"breakfast","comments":null}"""u8);

        Assert.Equal(new RecipeChoices(RecipeMeal.Breakfast, false, [], null, []) with
        {
            IncludeProductIds = choices!.IncludeProductIds, AvoidTitles = choices.AvoidTitles,
        }, choices);
        Assert.Empty(choices.IncludeProductIds);
        Assert.Empty(choices.AvoidTitles);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"meal":"supper"}""")]
    [InlineData("""{"meal":"snack","mode":"bake"}""")]
    [InlineData("""{"meal":"snack","mode":1}""")]
    [InlineData("""{"meal":"dinner","preferUseFirst":"yes"}""")]
    [InlineData("""{"meal":"dinner","includeProductIds":["not-an-id"]}""")]
    [InlineData("""{"meal":"dinner","includeProductIds":"x"}""")]
    [InlineData("""{"meal":"dinner","comments":5}""")]
    [InlineData("""{"meal":"dinner","avoidTitles":[1]}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    public void Bad_bodies_are_400s(string body) =>
        Assert.NotNull(RecipeFunctions.Parse(System.Text.Encoding.UTF8.GetBytes(body)).Error);

    [Fact]
    public void Results_map_to_statuses()
    {
        Assert.Equal(HttpStatusCode.BadRequest, RecipeFunctions.ToResponse(new SuggestRecipeResult.Invalid("x")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, RecipeFunctions.ToResponse(new SuggestRecipeResult.NothingInStock()).Status);
        Assert.Equal((HttpStatusCode)429, RecipeFunctions.ToResponse(new SuggestRecipeResult.TooManyRequests()).Status);

        // Unavailable is a 200 with the friendly message, so it never fires the 5xx error alert.
        var (status, body) = RecipeFunctions.ToResponse(new SuggestRecipeResult.Unavailable());
        Assert.Equal(HttpStatusCode.OK, status);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(body, WebJson)).RootElement;
        Assert.False(json.GetProperty("available").GetBoolean());
        Assert.Equal("Couldn't come up with a recipe just now. Try again.", json.GetProperty("message").GetString());
    }

    [Fact]
    public void A_recipe_is_returned_in_the_documented_shape()
    {
        var milk = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var recipe = new RecipeView("Penne", "Quick.", RecipeMeal.Dinner, 2, 25,
            [new("Semi-skimmed milk", "300 ml", milk, true), new("Oil", "1 tbsp", null, false)],
            ["Cook."], [new(milk, "Semi-skimmed milk", 2, new DateOnly(2026, 10, 20))], ["1 onion"], ["Cheddar"]);

        var (status, body) = RecipeFunctions.ToResponse(new SuggestRecipeResult.Suggested(recipe));

        Assert.Equal(HttpStatusCode.OK, status);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(body, WebJson)).RootElement;
        Assert.True(json.GetProperty("available").GetBoolean());
        var r = json.GetProperty("recipe");
        Assert.Equal("dinner", r.GetProperty("meal").GetString());
        Assert.Equal(milk, r.GetProperty("ingredients")[0].GetProperty("productId").GetGuid());
        Assert.True(r.GetProperty("ingredients")[0].GetProperty("useFirst").GetBoolean());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("ingredients")[1].GetProperty("productId").ValueKind);
        Assert.Equal("2026-10-20", r.GetProperty("usesUp")[0].GetProperty("soonestEstimatedExpiryDate").GetString());
        Assert.Equal("Cheddar", r.GetProperty("couldNotFitIn")[0].GetString());
    }

    [Fact]
    public void Mode_and_snack_are_read_and_mode_defaults_to_cook()
    {
        Assert.Equal((RecipeMeal.Snack, RecipeMode.ReadyNow),
            RecipeFunctions.Parse("""{"meal":"snack","mode":"readyNow"}"""u8).Choices is { } c ? (c.Meal, c.Mode) : default);
        Assert.Equal(RecipeMode.Cook, RecipeFunctions.Parse("""{"meal":"dinner","mode":"cook"}"""u8).Choices!.Mode);
        Assert.Equal(RecipeMode.Cook, RecipeFunctions.Parse("""{"meal":"dinner"}"""u8).Choices!.Mode);
    }

    [Fact]
    public void Ready_now_ideas_are_returned_in_the_documented_shape()
    {
        var hummus = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var ideas = new SuggestRecipeResult.IdeasSuggested(RecipeMeal.Snack,
        [
            new QuickIdeaView("Hummus and toasted pitta", "Toast 2 pittas and dip.", Appliance.Toaster, 4,
                [new(hummus, "Hummus", true, -1, new DateOnly(2026, 10, 19))], null),
            new QuickIdeaView("Hot chocolate", "Warm the milk.", Appliance.None, 3, [], "Hot chocolate powder"),
        ]);

        var (status, body) = RecipeFunctions.ToResponse(ideas);

        Assert.Equal(HttpStatusCode.OK, status);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(body, WebJson)).RootElement;
        Assert.Equal(("readyNow", "snack"), (json.GetProperty("mode").GetString(), json.GetProperty("meal").GetString()));
        var first = json.GetProperty("ideas")[0];
        Assert.Equal(("toaster", 4), (first.GetProperty("appliance").GetString(), first.GetProperty("minutes").GetInt32()));
        var use = first.GetProperty("uses")[0];
        Assert.Equal((hummus, true, -1), (use.GetProperty("productId").GetGuid(), use.GetProperty("useFirst").GetBoolean(), use.GetProperty("daysUntilSoonest").GetInt32()));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("alsoNeeded").ValueKind);
        Assert.Equal(("none", "Hot chocolate powder"),
            (json.GetProperty("ideas")[1].GetProperty("appliance").GetString(), json.GetProperty("ideas")[1].GetProperty("alsoNeeded").GetString()));
    }

    // ---- Adjust (#104)

    [Theory]
    [InlineData("Adjust")]
    [InlineData("SuggestAdditions")]
    public void Adjust_and_suggested_additions_need_a_signed_in_user(string method) =>
        Assert.True(FunctionAuthPolicy.RequiresUser($"CupboardTracker.Functions.Api.Recipes.RecipeFunctions.{method}"));

    private const string RecipeJson = """
        {"title":"Creamy penne","summary":"Quick.","meal":"dinner","servings":2,"totalMinutes":25,
         "ingredients":[{"name":"Penne","amount":"200 g","productId":"22222222-2222-2222-2222-222222222222","useFirst":false},
                        {"name":"Onion","amount":"1","productId":null}],
         "steps":["Cook it."],"alsoNeeded":["1 garlic clove"],"usesUp":[],"couldNotFitIn":[]}
        """;

    [Fact]
    public void An_adjustment_body_parses_the_recipe_and_the_changes()
    {
        var body = System.Text.Encoding.UTF8.GetBytes($$"""
            {"recipe":{{RecipeJson}},"add":{"productIds":["33333333-3333-3333-3333-333333333333"],"typed":["chilli flakes"]},
             "remove":["Onion"],"tweak":"spicier"}
            """);

        var (choices, error) = RecipeFunctions.ParseAdjust(body);

        Assert.Null(error);
        Assert.Equal(("Creamy penne", RecipeMeal.Dinner, 2), (choices!.Current.Title, choices.Current.Meal, choices.Current.Ingredients.Count));
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), choices.Current.Ingredients[0].ProductId);
        Assert.Null(choices.Current.Ingredients[1].ProductId);
        Assert.Equal([Guid.Parse("33333333-3333-3333-3333-333333333333")], choices.AddProductIds);
        Assert.Equal(["chilli flakes"], choices.AddTyped);
        Assert.Equal(["Onion"], choices.Remove);
        Assert.Equal("spicier", choices.Tweak);
    }

    [Theory]
    [InlineData("""{"add":{}}""")]
    [InlineData("""{"recipe":{"title":"x","meal":"dinner","ingredients":[]}}""")]
    [InlineData("""{"recipe":{"title":"x","meal":"supper","ingredients":[{"name":"a"}]}}""")]
    [InlineData("""{"recipe":{"title":"x","meal":"dinner","ingredients":[{"name":"a"}]},"add":{"productIds":["nope"]}}""")]
    [InlineData("""{"recipe":{"title":"x","meal":"dinner","ingredients":[{"name":"a"}]},"remove":"Onion"}""")]
    [InlineData("""{"recipe":{"title":"x","meal":"dinner","ingredients":[{"name":"a"}]},"tweak":5}""")]
    [InlineData("not json")]
    public void Bad_adjustment_bodies_are_400(string body) =>
        Assert.NotNull(RecipeFunctions.ParseAdjust(System.Text.Encoding.UTF8.GetBytes(body)).Error);

    [Fact]
    public void Suggested_additions_take_just_the_recipe()
    {
        var (recipe, error) = RecipeFunctions.ParseRecipeOnly(System.Text.Encoding.UTF8.GetBytes($$"""{"recipe":{{RecipeJson}}}"""));
        Assert.Null(error);
        Assert.Equal("Penne", recipe!.Ingredients[0].Name);
        Assert.NotNull(RecipeFunctions.ParseRecipeOnly("{}"u8).Error);
    }

    [Fact]
    public void Adjustment_responses()
    {
        var view = new RecipeView("Spicy penne", "Quick.", RecipeMeal.Dinner, 2, 25, [], ["Cook it."], [], [], [], ["Chorizo"], ["Onion"]);
        var (status, body) = RecipeFunctions.ToResponse(new AdjustRecipeResult.Adjusted(view, new AdjustmentSummary(["Chorizo"], ["Onion"], "spicier")));
        var json = JsonSerializer.SerializeToElement(body, WebJson);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Spicy penne", json.GetProperty("recipe").GetProperty("title").GetString());
        Assert.Equal("Chorizo", json.GetProperty("recipe").GetProperty("couldNotAdd")[0].GetString());
        Assert.Equal("Onion", json.GetProperty("recipe").GetProperty("couldNotRemove")[0].GetString());
        Assert.Equal("spicier", json.GetProperty("adjustmentSummary").GetProperty("tweak").GetString());
        Assert.Equal(HttpStatusCode.OK, RecipeFunctions.ToResponse(new AdjustRecipeResult.Unavailable()).Status);
        Assert.Equal(HttpStatusCode.BadRequest, RecipeFunctions.ToResponse(new AdjustRecipeResult.Invalid("x")).Status);
        Assert.Equal((HttpStatusCode)429, RecipeFunctions.ToResponse(new AdjustRecipeResult.TooManyRequests()).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, RecipeFunctions.ToResponse(new AdjustRecipeResult.NothingInStock()).Status);
    }
}
