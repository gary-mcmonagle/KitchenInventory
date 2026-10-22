using System.Text;
using System.Text.Json;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Functions.Api.Recipes;

namespace CupboardTracker.Functions.Api.Tests;

// The equipment chips on the API (#111).
public sealed class EquipmentParsingTests
{
    private static (IReadOnlySet<Kit>? Kit, string? Error) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return RecipeFunctions.ParseEquipment(document.RootElement);
    }

    [Fact]
    public void Missing_means_everything_older_apps_still_work()
    {
        Assert.Equal((null, null), Parse("{}"));
        Assert.Equal((null, null), Parse("""{"equipment":null}"""));
    }

    [Theory]
    [InlineData("""{"equipment":[]}""")]
    [InlineData("""{"equipment":["kettle"]}""")]
    [InlineData("""{"equipment":["hob",3]}""")]
    [InlineData("""{"equipment":"hob"}""")]
    public void Empty_or_unknown_is_a_400(string json) => Assert.NotNull(Parse(json).Error);

    [Fact]
    public void Suggest_and_adjust_carry_it()
    {
        var (choices, error) = RecipeFunctions.Parse(Encoding.UTF8.GetBytes("""{"meal":"dinner","equipment":["hob","microwave"]}"""));
        Assert.Null(error);
        Assert.Equal(new HashSet<Kit> { Kit.Hob, Kit.Microwave }, choices!.Equipment);

        var (adjust, adjustError) = RecipeFunctions.ParseAdjust(Encoding.UTF8.GetBytes("""
            {"recipe":{"title":"Pasta","summary":"","meal":"dinner","servings":2,"totalMinutes":20,
                       "ingredients":[{"name":"Penne","amount":"200 g","productId":null}],"steps":["Cook it."],"alsoNeeded":[]},
             "tweak":"in the oven","equipment":["hob"]}
            """));
        Assert.Null(adjustError);
        Assert.Equal(new HashSet<Kit> { Kit.Hob }, adjust!.Equipment);

        Assert.NotNull(RecipeFunctions.Parse(Encoding.UTF8.GetBytes("""{"meal":"dinner","equipment":[]}""")).Error);
    }

    // ---- Meat or veg? (#110)

    [Fact]
    public void Diet_is_any_when_missing_and_half_is_cook_only()
    {
        Assert.Equal(Diet.Any, RecipeFunctions.Parse(Encoding.UTF8.GetBytes("""{"meal":"dinner"}""")).Choices!.Diet);
        Assert.Equal(Diet.Veg, RecipeFunctions.Parse(Encoding.UTF8.GetBytes("""{"meal":"dinner","mode":"readyNow","diet":"veg"}""")).Choices!.Diet);
        Assert.Equal(Diet.Half, RecipeFunctions.Parse(Encoding.UTF8.GetBytes("""{"meal":"dinner","diet":"half"}""")).Choices!.Diet);
        Assert.NotNull(RecipeFunctions.Parse(Encoding.UTF8.GetBytes("""{"meal":"dinner","mode":"readyNow","diet":"half"}""")).Error);
        Assert.NotNull(RecipeFunctions.Parse(Encoding.UTF8.GetBytes("""{"meal":"dinner","diet":"vegan"}""")).Error);
    }

    [Fact]
    public void Adjust_reads_the_diet_and_the_new_recipe_shapes_with_old_string_steps_still_accepted()
    {
        var (adjust, error) = RecipeFunctions.ParseAdjust(Encoding.UTF8.GetBytes("""
            {"recipe":{"title":"Pasta","summary":"","meal":"dinner","servings":2,"servingsMeat":1,"servingsVeg":1,"totalMinutes":20,"diet":"half",
                       "ingredients":[{"name":"Bacon","amount":"2","productId":null,"for":"meat"}],
                       "steps":[{"text":"Fry the bacon.","for":"meat"},"Cook it."],"alsoNeeded":[{"name":"1 onion","for":"all"}]},
             "tweak":"add bacon","diet":"half"}
            """));

        Assert.Null(error);
        Assert.Equal(Diet.Half, adjust!.Diet);
        Assert.Equal([new RecipeStep("Fry the bacon.", PlateFor.Meat), new RecipeStep("Cook it.")], adjust.Current.Steps);
        Assert.Equal(PlateFor.Meat, adjust.Current.Ingredients[0].For);
        Assert.Equal((1, 1), (adjust.Current.ServingsMeat, adjust.Current.ServingsVeg));
    }

    [Fact]
    public void Idea_adjust_refuses_half_and_half() =>
        Assert.NotNull(IdeaAdjustFunctions.Parse(Encoding.UTF8.GetBytes("""
            {"idea":{"title":"x","how":"y","appliance":"toaster","uses":[]},"meal":"snack","tweak":"z","diet":"half"}
            """)).Error);

    [Fact]
    public void No_veggie_ideas_is_a_friendly_200()
    {
        var (status, body) = RecipeFunctions.ToResponse(new SuggestRecipeResult.NoVeggieIdeas());
        Assert.Equal(System.Net.HttpStatusCode.OK, status);
        Assert.Contains("find veggie ideas in your stock.", JsonSerializer.Serialize(body), StringComparison.Ordinal);
    }
}
