using CupboardTracker.Domain.Products;

namespace CupboardTracker.Domain.Tests;

public sealed class GenericNamesTests
{
    [Theory]
    [InlineData("  Semi-skimmed   Milk ", "semi-skimmed milk")]
    [InlineData("Dijon mustard", "dijon mustard")]
    public void Ids_are_lower_case_trimmed_with_single_spaces(string name, string id) => Assert.Equal(id, GenericNames.IdOf(name));

    [Fact]
    public void Display_is_sentence_case_keeping_proper_nouns() =>
        Assert.Equal(("Udon noodles", "Parmigiano Reggiano"), (GenericNames.Display("udon  noodles"), GenericNames.Display("Parmigiano Reggiano")));

    [Theory]
    [InlineData("Bran flakes", null, null)]
    [InlineData("Chinese 5 spice", null, null)] // a bare number isn't a size
    [InlineData("", null, "1 to 40")]
    [InlineData("A name that is far too long to fit on any shopping list", null, "1 to 40")]
    [InlineData("Baked beans 415g", null, "size")]
    [InlineData("Milk 4 pints", null, "size")]
    [InlineData("Pizza subs 2 x 125 g", null, "size")]
    [InlineData("Heinz baked beans", "Heinz", "brand")]
    public void Names_are_checked(string name, string? brand, string? problem)
    {
        var actual = GenericNames.Problem(name, brand);
        if (problem is null)
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.Contains(problem, actual!, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Chopped Tomatoes 400g", "Chopped Tomatoes")]
    [InlineData("Vegetable Stock Cubes 12 x 10g", "Vegetable Stock Cubes")]
    [InlineData("garden peas", "Garden peas")]
    public void The_fallback_is_the_display_name_without_sizes(string displayName, string fallback) =>
        Assert.Equal(fallback, GenericNames.FallbackFrom(displayName));

    [Fact]
    public void Words_ignore_brand_sizes_and_filler_and_fold_plurals()
    {
        Assert.Equal(["semi", "skimmed", "milk"], GenericNames.Words("Asda Semi Skimmed Milk 2L", "Asda").Order().Reverse().OrderBy(w => w == "semi" ? 0 : w == "skimmed" ? 1 : 2));
        Assert.Equal(["tomato", "chopped"], GenericNames.Words("The Chopped Tomatoes 400 g pack").OrderByDescending(w => w));
        Assert.Contains("bean", GenericNames.Words("Taco Mixed Beans"));
    }

    [Fact]
    public void Products_need_a_generic_name_once_known_with_an_aisle_and_a_manual_one_sticks()
    {
        var at = DateTimeOffset.UnixEpoch;
        var product = Product.Loose(Guid.NewGuid(), "Leeks", null, null, ProductSource.Manual, Guid.NewGuid(), at);
        Assert.False(product.NeedsGenericName); // no aisle yet

        var withAisle = product with { Aisle = new ProductAisle("fruitVeg", "jev", 0.9, at) };
        Assert.True(withAisle.NeedsGenericName);

        var manual = withAisle.WithManualGenericName(GenericName.Create("Leeks", "fruitVeg", at, "manual"), at);
        Assert.False(manual.NeedsGenericName);
        Assert.True(manual.Generic!.IsManual);
        Assert.Equal(("leeks", "Leeks"), (manual.Generic.Id, manual.Generic.Name));
    }
}
