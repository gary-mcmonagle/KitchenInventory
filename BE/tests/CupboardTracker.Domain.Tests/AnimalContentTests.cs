using CupboardTracker.Domain.Products;

namespace CupboardTracker.Domain.Tests;

// The Open Food Facts category rule (#112): decisive categories only; everything else goes to Jev.
public sealed class AnimalContentTests
{
    [Theory]
    [InlineData(new[] { "en:meals", "en:vegetarian-meals" }, AnimalContent.None)]
    [InlineData(new[] { "en:plant-based-foods", "en:vegan-products" }, AnimalContent.None)]
    [InlineData(new[] { "en:meat-analogues", "en:vegetarian-sausages" }, AnimalContent.None)] // veg wins over "sausages"
    [InlineData(new[] { "en:seafood", "en:prawns" }, AnimalContent.Fish)]
    [InlineData(new[] { "en:canned-fishes", "en:tunas" }, AnimalContent.Fish)]
    [InlineData(new[] { "en:meats", "en:prepared-meats", "en:hams" }, AnimalContent.Meat)]
    [InlineData(new[] { "en:pork-sausages" }, AnimalContent.Meat)]
    [InlineData(new[] { "en:chickens" }, AnimalContent.Meat)]
    public void Decisive_categories_answer(string[] tags, AnimalContent expected) => Assert.Equal(expected, AnimalContents.FromCategoryTags(tags));

    [Theory]
    [InlineData("")]
    [InlineData("en:plant-based-foods-and-beverages,en:broths")] // stock cubes: Jev decides
    [InlineData("en:snacks,en:chicken-flavoured-crisps")] // flavour isn't meat
    [InlineData("en:sauces,en:worcestershire-sauces")]
    public void Anything_else_is_left_to_jev(string tags) =>
        Assert.Null(AnimalContents.FromCategoryTags(tags.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void Ids_round_trip_and_fish_counts_as_meat_for_veg()
    {
        Assert.All(Enum.GetValues<AnimalContent>(), v => Assert.Equal(v, AnimalContents.Parse(AnimalContents.Id(v))));
        Assert.True(AnimalContents.IsMeatOrFish(AnimalContent.Fish));
        Assert.False(AnimalContents.IsMeatOrFish(AnimalContent.Unknown));
    }
}
