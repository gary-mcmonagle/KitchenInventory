using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CupboardTracker.Application.Plans;
using CupboardTracker.Functions.Api.Plans;

namespace CupboardTracker.Functions.Api.Tests;

// Plan meals (#119): the request shapes and the status each outcome maps to.
public sealed class MealPlanFunctionsTests
{
    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void A_create_request_reads_the_choices_and_prefer_use_first_is_off_when_missing()
    {
        var (choices, error) = MealPlanFunctions.ParseChoices(Body("""{"days":3,"meals":["dinner","lunch"],"diet":"half","equipment":["hob","blender"],"comments":"quick"}"""));

        Assert.Null(error);
        Assert.Equal((3, "half", false, "quick"), (choices!.Days, choices.Diet, choices.PreferUseFirst, choices.Comments));
        Assert.Equal(["dinner", "lunch"], choices.MealTypes);
        Assert.Equal(["hob", "blender"], choices.Equipment);
        Assert.True(MealPlanFunctions.ParseChoices(Body("""{"days":1,"meals":["dinner"],"preferUseFirst":true}""")).Choices!.PreferUseFirst);
        Assert.False(MealPlanFunctions.ParseChoices(Body("""{"days":1,"meals":["dinner"],"preferUseFirst":"yes"}""")).Choices!.PreferUseFirst);
    }

    [Fact]
    public void Diet_defaults_to_any_and_missing_equipment_means_all_kit()
    {
        var choices = MealPlanFunctions.ParseChoices(Body("""{"days":2,"meals":["breakfast"]}""")).Choices!;

        Assert.Equal("any", choices.Diet);
        Assert.Null(choices.Equipment);
        Assert.Null(MealPlanFunctions.ParseChoices(Body("""{"days":2,"meals":["breakfast"],"equipment":null}""")).Choices!.Equipment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"meals":["dinner"]}""")]
    [InlineData("""{"days":"3","meals":["dinner"]}""")]
    [InlineData("""{"days":3,"meals":"dinner"}""")]
    [InlineData("""{"days":3,"meals":[1]}""")]
    [InlineData("""{"days":3,"meals":["dinner"],"equipment":"hob"}""")]
    public void A_malformed_create_request_is_refused_with_the_shape(string json)
    {
        var (choices, error) = MealPlanFunctions.ParseChoices(Body(json));

        Assert.Null(choices);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("""{"days":9,"meals":["dinner"]}""", "Plan 1 to 7 days.")]
    [InlineData("""{"days":2,"meals":[]}""", "Pick at least one of breakfast, lunch and dinner.")]
    [InlineData("""{"days":2,"meals":["dinner"],"diet":"vegan"}""", "Diet must be any, veg or half.")]
    [InlineData("""{"days":2,"meals":["dinner"],"equipment":["laser"]}""", "Unknown kit.")]
    public void Bad_choices_say_what_is_wrong(string json, string expected) =>
        Assert.Equal(expected, MealPlanFunctions.ParseChoices(Body(json)).Error);

    [Fact]
    public void A_body_that_is_too_large_is_refused()
    {
        var big = $$"""{"days":2,"meals":["dinner"],"comments":"{{new string('x', MealPlanFunctions.MaxBodyBytes)}}"}""";

        Assert.NotNull(MealPlanFunctions.ParseChoices(Body(big)).Error);
    }

    private static readonly MealPlan Plan = new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"), Guid.NewGuid(), "Gary", new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero), PlanStatuses.Ready, null, new PlanChoices(2, ["dinner"], "any", null, true, null),
        [
            new PlannedMeal("dinner-1", "dinner", "Chilli", "A chilli.", 2, [new PlannedProduct(Guid.NewGuid(), "Beans")], [new PlannedProduct(Guid.NewGuid(), "Milk")],
                ["peppers"], false, ["w"], new JsonObject { ["title"] = "Full recipe" }),
            new PlannedMeal("dinner-2", "dinner", "Pasta", "Pasta.", 2, [], [], ["peppers"], true, []),
        ]);

    [Fact]
    public void A_plan_is_returned_with_each_meals_facts_and_without_the_full_recipe()
    {
        var json = MealPlanFunctions.ToJson(MealPlans.ToView(Plan));

        Assert.Equal("ready", (string?)json["status"]);
        Assert.Equal("2026-10-28T18:00:00.000Z", (string?)json["createdAt"]);
        Assert.Equal(true, (bool?)json["choices"]!["preferUseFirst"]);
        var meal = json["meals"]![0]!.AsObject();
        Assert.False(meal.ContainsKey("recipe"));
        Assert.Equal(true, (bool?)meal["hasRecipe"]);
        Assert.Equal(false, (bool?)json["meals"]![1]!["hasRecipe"]);
        Assert.Equal(1, (int?)meal["usesCount"]);
        Assert.Equal(["peppers"], meal["shares"]!["names"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(1, (int?)meal["shares"]!["otherMeals"]);
        Assert.Equal(["Milk"], meal["useFirst"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["w"], meal["warnings"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(true, (bool?)json["meals"]![1]!["keep"]);
        Assert.Equal(["peppers"], json["meals"]![1]!["shares"]!["names"]!.AsArray().Select(n => (string?)n)); // shared with the first meal too
    }

    [Fact]
    public void Each_outcome_has_its_status_and_a_friendly_error()
    {
        var view = MealPlans.ToView(Plan);

        Assert.Equal(HttpStatusCode.Accepted, MealPlanFunctions.ToResponse(new PlanOutcome.Done(view), created: true).Status);
        Assert.Equal(HttpStatusCode.OK, MealPlanFunctions.ToResponse(new PlanOutcome.Done(view)).Status);
        var (status, body) = MealPlanFunctions.ToResponse(new PlanOutcome.MealDeleted(view, Plan.Meals[0]));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Chilli", (string?)body["meal"]!["title"]);
        Assert.Equal("Full recipe", (string?)body["meal"]!["recipe"]!["title"]); // for Undo
        Assert.NotNull(body["plan"]);

        (HttpStatusCode, string)[] cases =
        [
            (MealPlanFunctions.ToResponse(new PlanOutcome.Invalid("Nope.")).Status, "Nope."),
            (MealPlanFunctions.ToResponse(new PlanOutcome.NotFound("No such plan.")).Status, "No such plan."),
            (MealPlanFunctions.ToResponse(new PlanOutcome.NotReady("Still being made.")).Status, "Still being made."),
            (MealPlanFunctions.ToResponse(new PlanOutcome.Conflict()).Status, "The plan changed while this was running. Try again."),
            (MealPlanFunctions.ToResponse(new PlanOutcome.NothingInStock()).Status, "There's nothing in stock to plan from."),
            (MealPlanFunctions.ToResponse(new PlanOutcome.TooManyRequests()).Status, MealPlanFunctions.TooManyMessage),
            (MealPlanFunctions.ToResponse(new PlanOutcome.Busy()).Status, "Busy right now, try again in a minute."),
            (MealPlanFunctions.ToResponse(new PlanOutcome.Unavailable()).Status, "Couldn't plan just now. Try again."),
        ];
        Assert.Equal(
            [HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.Conflict, HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity,
             (HttpStatusCode)429, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable],
            cases.Select(c => c.Item1));
        Assert.Equal("Nope.", (string?)MealPlanFunctions.ToResponse(new PlanOutcome.Invalid("Nope.")).Body["error"]);
        Assert.Equal("Busy right now, try again in a minute.", (string?)MealPlanFunctions.ToResponse(new PlanOutcome.Busy()).Body["error"]);
    }
}
