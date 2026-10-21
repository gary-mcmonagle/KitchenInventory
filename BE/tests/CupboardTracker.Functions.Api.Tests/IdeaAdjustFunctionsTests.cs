using System.Net;
using System.Text;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Recipes;

namespace CupboardTracker.Functions.Api.Tests;

// Adjust a Ready now idea (#108).
public sealed class IdeaAdjustFunctionsTests
{
    private static readonly Guid Beans = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static (IdeaAdjustChoices? Choices, string? Error) Parse(string json) => IdeaAdjustFunctions.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void It_needs_a_signed_in_user() =>
        Assert.True(FunctionAuthPolicy.RequiresUser("CupboardTracker.Functions.Api.Recipes.IdeaAdjustFunctions.Adjust"));

    [Fact]
    public void The_idea_meal_and_changes_are_read()
    {
        var (choices, error) = Parse($$"""
            {"idea":{"title":"Beans on toast","how":"Toast it.","appliance":"microwave","minutes":5,
                     "uses":[{"productId":"{{Beans}}","displayName":"Baked beans"}],"alsoNeeded":null},
             "meal":"snack","add":{"productIds":[],"typed":["cheese"]},"remove":["Baked beans"],"tweak":"spicier"}
            """);

        Assert.Null(error);
        Assert.Equal(("Beans on toast", Appliance.Microwave, RecipeMeal.Snack), (choices!.Current.Title, choices.Current.Appliance, choices.Meal));
        Assert.Equal([new IdeaUse(Beans, "Baked beans")], choices.Current.Uses);
        Assert.Equal(["cheese"], choices.AddTyped);
        Assert.Equal(["Baked beans"], choices.Remove);
        Assert.Equal("spicier", choices.Tweak);
    }

    [Theory]
    [InlineData("""{"meal":"snack"}""")]
    [InlineData("""{"idea":{"title":"x","how":"y","appliance":"hob","uses":[]},"meal":"snack"}""")]
    [InlineData("""{"idea":{"title":"x","how":"y","appliance":"toaster","uses":["nope"]},"meal":"snack"}""")]
    [InlineData("""{"idea":{"title":"x","how":"y","appliance":"toaster","uses":[]},"meal":"brunch"}""")]
    [InlineData("""{"idea":{"title":"x","how":"y","appliance":"toaster","uses":[]},"meal":"snack","tweak":3}""")]
    [InlineData("not json")]
    public void Bad_bodies_are_400s(string json) => Assert.NotNull(Parse(json).Error);

    [Fact]
    public void Results_map_to_statuses()
    {
        var idea = new QuickIdeaView("Cheesy beans", "Melt it.", Appliance.Toaster, 6, [], null);
        var (status, _) = IdeaAdjustFunctions.ToResponse(new AdjustIdeaResult.Adjusted(idea, new AdjustmentSummary(["Cheddar"], [], null), [], []));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.OK, IdeaAdjustFunctions.ToResponse(new AdjustIdeaResult.Unavailable()).Status);
        Assert.Equal(HttpStatusCode.BadRequest, IdeaAdjustFunctions.ToResponse(new AdjustIdeaResult.Invalid("x")).Status);
        Assert.Equal((HttpStatusCode)429, IdeaAdjustFunctions.ToResponse(new AdjustIdeaResult.TooManyRequests()).Status);
    }
}
