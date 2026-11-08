using System.Text.Json.Nodes;
using CupboardTracker.Application.Ratings;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Infrastructure.Cosmos;

namespace CupboardTracker.Infrastructure.Tests;

// Ratings (#128): the ratings documents.
public sealed class RatingStorageTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_rating_is_one_document_in_the_household_partition_and_round_trips()
    {
        var rating = new Rating(Guid.NewGuid(), Guid.NewGuid(), "Sam", RatingValues.Down, [RatingReasons.Ingredient, RatingReasons.TooSpicy], ["Peppers"],
            RatingContexts.AfterCooking, At, At.AddMinutes(1),
            new RatingSnapshot(RatingKinds.PlannedMeal, "Halloumi  Traybake", RecipeMeal.Dinner, Diet.Half, 40,
                [new RatingIngredient("Peppers", "Peppers"), new RatingIngredient("Salt", null)], ["ovenGrill"], Guid.NewGuid(), Guid.NewGuid(), "dinner-2"));

        var document = CosmosRatingRepository.ToDocument(rating);

        Assert.Equal(("household", "down", "halloumi traybake", "dinner", "half"),
            (document["householdId"]!.GetValue<string>(), document["rating"]!.GetValue<string>(), document["normalizedTitle"]!.GetValue<string>(),
                document["meal"]!.GetValue<string>(), document["diet"]!.GetValue<string>()));
        Assert.Null(document["embedding"]); // written by a later card
        Assert.DoesNotContain("steps", document.ToJsonString());
        Assert.DoesNotContain("amount", document.ToJsonString());
        var back = CosmosRatingRepository.FromDocument(JsonNode.Parse(document.ToJsonString())!.AsObject());
        Assert.Equal(rating with { Reasons = [], DislikedIngredients = [], Snapshot = rating.Snapshot with { Ingredients = [], Equipment = [] } },
            back with { Reasons = [], DislikedIngredients = [], Snapshot = back.Snapshot with { Ingredients = [], Equipment = [] } });
        Assert.Equal(rating.Reasons, back.Reasons);
        Assert.Equal(rating.DislikedIngredients, back.DislikedIngredients);
        Assert.Equal(rating.Snapshot.Ingredients, back.Snapshot.Ingredients);
        Assert.Equal(rating.Snapshot.Equipment, back.Snapshot.Equipment);
    }

    [Fact]
    public void Links_are_only_written_when_there_are_any()
    {
        var rating = new Rating(Guid.NewGuid(), Guid.NewGuid(), "Gary", RatingValues.Up, [], [], RatingContexts.Suggested, At, At,
            new RatingSnapshot(RatingKinds.Recipe, "Penne", RecipeMeal.Dinner, Diet.Any, 20, [], []));

        var document = CosmosRatingRepository.ToDocument(rating);

        Assert.Null(document["savedRecipeId"]);
        Assert.Null(document["planId"]);
        Assert.Null(document["mealId"]);
        Assert.Null(CosmosRatingRepository.FromDocument(JsonNode.Parse(document.ToJsonString())!.AsObject()).Snapshot.PlanId);
    }
}
