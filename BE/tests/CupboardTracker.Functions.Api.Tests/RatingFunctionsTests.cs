using System.Text.Json;
using CupboardTracker.Application.Ratings;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Ratings;

namespace CupboardTracker.Functions.Api.Tests;

// Ratings (#128).
public sealed class RatingFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("List")]
    [InlineData("Put")]
    [InlineData("Patch")]
    [InlineData("Delete")]
    public void All_need_a_signed_in_user(string method) =>
        Assert.True(FunctionAuthPolicy.RequiresUser($"CupboardTracker.Functions.Api.Ratings.RatingFunctions.{method}"));

    [Fact]
    public void A_rating_body_parses()
    {
        using var body = JsonDocument.Parse("""
            {"rating":"down","context":"afterCooking","kind":"plannedMeal","title":"Penne","meal":"dinner","totalMinutes":25,"diet":"half",
             "reasons":["ingredient","tooSpicy"],"dislikedIngredients":["Peppers"],
             "ingredients":[{"name":"Penne","genericName":"Penne"},{"name":"Salt"}],"equipment":["hob"],
             "planId":"22222222-2222-2222-2222-222222222222","mealId":"dinner-1"}
            """);

        var (put, error) = RatingFunctions.ParsePut(Guid.NewGuid(), body);

        Assert.Null(error);
        Assert.Equal(("down", "afterCooking", RatingKinds.PlannedMeal, Diet.Half), (put!.Value, put.Context, put.Snapshot.Kind, put.Snapshot.Diet));
        Assert.Equal([RatingReasons.Ingredient, RatingReasons.TooSpicy], put.Reasons);
        Assert.Equal(["Peppers"], put.DislikedIngredients);
        Assert.Equal(("Penne", "Penne"), (put.Snapshot.Ingredients[0].Name, put.Snapshot.Ingredients[0].GenericName));
        Assert.Null(put.Snapshot.Ingredients[1].GenericName);
        Assert.Equal((Guid.Parse("22222222-2222-2222-2222-222222222222"), "dinner-1", "hob"), (put.Snapshot.PlanId, put.Snapshot.MealId, put.Snapshot.Equipment[0]));
    }

    [Fact]
    public void Reasons_ingredients_equipment_and_diet_are_optional()
    {
        using var body = JsonDocument.Parse("""{"rating":"up","context":"suggested","kind":"recipe","title":"Penne","meal":"dinner","totalMinutes":25}""");

        var (put, error) = RatingFunctions.ParsePut(Guid.NewGuid(), body);

        Assert.Null(error);
        Assert.Equal((Diet.Any, 0, 0, 0), (put!.Snapshot.Diet, put.Reasons.Count, put.Snapshot.Ingredients.Count, put.Snapshot.Equipment.Count));
    }

    [Theory]
    [InlineData("""{"context":"suggested","kind":"recipe","title":"x","meal":"dinner","totalMinutes":5}""")]
    [InlineData("""{"rating":"up","context":"suggested","kind":"recipe","title":"x","meal":"supper","totalMinutes":5}""")]
    [InlineData("""{"rating":"up","context":"suggested","kind":"recipe","title":"x","meal":"dinner"}""")]
    [InlineData("""{"rating":"up","context":"suggested","kind":"recipe","title":"x","meal":"dinner","totalMinutes":5,"reasons":"tooSpicy"}""")]
    [InlineData("""{"rating":"up","context":"suggested","kind":"recipe","title":"x","meal":"dinner","totalMinutes":5,"ingredients":[{"genericName":"y"}]}""")]
    [InlineData("""{"rating":"up","context":"suggested","kind":"recipe","title":"x","meal":"dinner","totalMinutes":5,"diet":"keto"}""")]
    public void A_bad_body_is_refused(string json)
    {
        using var body = JsonDocument.Parse(json);

        var (put, error) = RatingFunctions.ParsePut(Guid.NewGuid(), body);

        Assert.Null(put);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_rating_is_returned_without_the_persons_id()
    {
        var rating = new Rating(Guid.NewGuid(), Guid.NewGuid(), "Sam", RatingValues.Down, [RatingReasons.NotFilling], [], RatingContexts.Saved, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new RatingSnapshot(RatingKinds.Recipe, "Penne", RecipeMeal.Lunch, Diet.Veg, 20, [new("Penne", null)], []));

        var json = JsonSerializer.Serialize(RatingFunctions.ToJson(rating, mine: false), WebJson);

        Assert.DoesNotContain("personId", json);
        Assert.Contains("\"personName\":\"Sam\"", json);
        Assert.Contains("\"mine\":false", json);
        Assert.Contains("\"key\":\"penne\"", json);
    }
}
