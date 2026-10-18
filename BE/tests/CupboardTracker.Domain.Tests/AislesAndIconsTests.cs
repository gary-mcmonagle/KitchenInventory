using System.Text.Json;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Domain.Tests;

public sealed class AislesAndIconsTests
{
    [Fact]
    public void Aisles_are_in_shop_order_with_stable_ids_and_other_last()
    {
        Assert.Equal(
            ["fruitVeg", "bakery", "meatFish", "dairyEggs", "chilledDeli", "frozen", "pastaRiceGrains", "tinsJars", "saucesOils",
             "bakingSpices", "breakfastCereal", "snacksSweets", "drinks", "other"],
            Aisles.All.Select(a => a.Id));
        Assert.Equal(Aisles.Other, Aisles.Of(null));
        Assert.Equal(Aisles.Other, Aisles.Of("garden"));
        Assert.Equal(0, Aisles.Order("fruitVeg"));
        Assert.Equal(13, Aisles.Order(null));
    }

    [Fact]
    public void The_curated_icons_are_iconparks_foods_plus_extras_and_the_ui_bundles_every_one_and_each_aisle_icon()
    {
        Assert.Equal(131, ProductIcons.Names.Count);
        Assert.Contains("bread", ProductIcons.Names);
        Assert.Contains("seedling", ProductIcons.Names);
        Assert.False(ProductIcons.IsKnown("rocket"));

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "FE", "src", "productIcons.json")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repo root not found");
        }

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "FE", "src", "productIcons.json")));
        var bundled = json.RootElement.GetProperty("icons").EnumerateObject().Select(p => p.Name).ToHashSet();
        var missing = ProductIcons.Names.Concat(Aisles.All.Select(a => a.Icon)).Where(n => !bundled.Contains(n)).ToList();
        Assert.True(missing.Count == 0, "Not bundled (run FE/scripts/build-icons.mjs): " + string.Join(", ", missing));
    }
}
