using CupboardTracker.Application.Recipes;

namespace CupboardTracker.Application.Tests.Recipes;

// Which kit a method step uses (#111): the deterministic check behind "Check before cooking".
public sealed class EquipmentTests
{
    [Theory]
    [InlineData("Preheat the oven to 200°C (180°C fan).", Kit.OvenGrill, true)]
    [InlineData("Roast for 25 minutes until golden.", Kit.OvenGrill, true)]
    [InlineData("Bake for 20 minutes.", Kit.OvenGrill, true)]
    [InlineData("Put under a hot grill for 3 minutes.", Kit.OvenGrill, true)]
    [InlineData("Cook at gas mark 6.", Kit.OvenGrill, true)]
    [InlineData("Add the grilled peppers and the baked beans.", Kit.OvenGrill, false)] // ingredients, not methods
    [InlineData("Scatter over the roasted nuts.", Kit.OvenGrill, false)]
    [InlineData("Mix in the baking powder.", Kit.OvenGrill, false)]
    [InlineData("Preheat the air fryer to 180°C and cook for 12 minutes.", Kit.OvenGrill, false)]
    [InlineData("Fry the onion in a frying pan for 5 minutes.", Kit.Hob, true)]
    [InlineData("Simmer for 10 minutes.", Kit.Hob, true)]
    [InlineData("Bring a saucepan of water to the boil.", Kit.Hob, true)]
    [InlineData("Boil the kettle and pour over the couscous.", Kit.Hob, false)]
    [InlineData("Pour boiling water from the kettle over the noodles.", Kit.Hob, false)]
    [InlineData("Air fry the chips for 15 minutes.", Kit.Hob, false)]
    [InlineData("Cook in the air fryer at 200°C.", Kit.AirFryer, true)]
    [InlineData("Air-fry the halloumi.", Kit.AirFryer, true)]
    [InlineData("Microwave for 2 minutes.", Kit.Microwave, true)]
    [InlineData("Pop the bread in the toaster.", Kit.Toaster, true)]
    [InlineData("Toast the bread under the grill.", Kit.Toaster, false)]
    [InlineData("Cook on low in the slow cooker for 6 hours.", Kit.SlowCooker, true)]
    [InlineData("Cook the rice in the rice cooker.", Kit.RiceCooker, true)]
    [InlineData("Add everything to the soup maker.", Kit.SoupMaker, true)]
    [InlineData("Blitz until smooth.", Kit.Blender, true)]
    [InlineData("Stir until smooth.", Kit.Blender, false)]
    public void Steps_are_read_for_the_kit_they_use(string step, Kit kit, bool uses) => Assert.Equal(uses, Kits.Mentions(step, kit));

    [Fact]
    public void Only_kit_that_isnt_allowed_is_reported_once_from_the_steps_or_the_declared_list()
    {
        var allowed = new HashSet<Kit> { Kit.Hob, Kit.Microwave };
        string[] steps = ["Fry the onion.", "Roast the peppers.", "Bake the lot."];

        Assert.Equal([Kit.OvenGrill, Kit.Blender], Kits.NotAllowed(steps, [Kit.Hob, Kit.Blender], allowed));
        Assert.Empty(Kits.NotAllowed(steps, [], Kits.All));
    }

    [Fact]
    public void Ids_labels_and_warnings()
    {
        Assert.Equal(["hob", "ovenGrill", "microwave", "toaster", "airFryer", "slowCooker", "riceCooker", "soupMaker", "blender"],
            Enum.GetValues<Kit>().Select(Kits.Id));
        Assert.All(Enum.GetValues<Kit>(), k => Assert.Equal(k, Kits.Parse(Kits.Id(k))));
        Assert.Null(Kits.Parse("kettle")); // always allowed, never a chip
        Assert.Equal("Oven & grill", Kits.Label(Kit.OvenGrill));
        Assert.Equal("This recipe mentions the oven or grill, which you said you can't use.", Kits.Warning(Kit.OvenGrill));
        Assert.Equal("airFryer", Appliances.Id(Appliance.AirFryer));
        Assert.Equal(Appliance.AirFryer, Appliances.Parse("airFryer"));
    }
}
