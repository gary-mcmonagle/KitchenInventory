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
}
