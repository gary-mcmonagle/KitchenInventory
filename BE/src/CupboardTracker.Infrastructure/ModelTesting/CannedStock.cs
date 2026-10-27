using CupboardTracker.Application.Recipes;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Infrastructure.ModelTesting;

/// <summary>
/// The stock the model test suite cooks from (#121): twelve made-up generic items, fixed in code. Nothing here comes from
/// the household's data, and nothing is stored: product IDs are constants that exist nowhere in Cosmos.
/// </summary>
public static class CannedStock
{
    public static readonly IReadOnlyList<RecipeStockItem> Items =
    [
        Item(1, "Chicken thighs", 4, 2, true, "Meat and fish", AnimalContent.Meat),
        Item(2, "Pork sausages", 8, 4, false, "Meat and fish", AnimalContent.Meat),
        Item(3, "Cheddar", 1, 20, false, "Dairy and eggs", AnimalContent.None),
        Item(4, "Eggs", 6, 9, false, "Dairy and eggs", AnimalContent.None),
        Item(5, "Brown onions", 3, 14, false, "Fruit and veg", AnimalContent.None),
        Item(6, "Chopped tomatoes", 2, 300, false, "Tins and jars", AnimalContent.None),
        Item(7, "Pasta", 1, 400, false, "Cupboard", AnimalContent.None),
        Item(8, "Basmati rice", 1, 400, false, "Cupboard", AnimalContent.None),
        Item(9, "Potatoes", 6, 12, false, "Fruit and veg", AnimalContent.None),
        Item(10, "Carrots", 4, 10, false, "Fruit and veg", AnimalContent.None),
        Item(11, "Baked beans", 2, 250, false, "Tins and jars", AnimalContent.None),
        Item(12, "Spinach", 1, 1, true, "Fruit and veg", AnimalContent.None),
    ];

    /// <summary>The hardest request: Half &amp; half for dinner, with the oven &amp; grill and the slow cooker unticked.</summary>
    public static RecipeRequest HalfAndHalf { get; } = new(
        RecipeMeal.Dinner, true, Items, [], null, [],
        new HashSet<Kit>(Kits.All.Where(k => k is not (Kit.OvenGrill or Kit.SlowCooker))), Diet.Half);

    /// <summary>Ready now ideas ask for no cooking kit, so the diet and equipment are the defaults.</summary>
    public static RecipeRequest ReadyNow { get; } = new(RecipeMeal.Snack, true, Items, [], null, []);

    private static RecipeStockItem Item(int n, string name, int quantity, int days, bool useFirst, string aisle, AnimalContent animal) =>
        new(new Guid($"00000000-0000-4000-8000-{n:D12}"), name, quantity, days, useFirst, aisle, 0, animal);
}
