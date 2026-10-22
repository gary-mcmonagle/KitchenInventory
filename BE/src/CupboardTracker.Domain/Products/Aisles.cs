namespace CupboardTracker.Domain.Products;

/// <summary>A supermarket aisle (#71): how the stock list is grouped. <see cref="Icon"/> is its IconPark icon (#72),
/// shown for products whose own icon Jev wasn't sure of.</summary>
public sealed record Aisle(string Id, string Name, string Icon, string Description);

/// <summary>
/// The fixed list of aisles, in shop order (#71, agreed with Gary). IDs are stable; names may be tweaked. A product
/// with no aisle yet is shown under <see cref="Other"/>.
/// </summary>
public static class Aisles
{
    /// <summary>Raw meat and fish (#112 uses it: anything here is meat or fish).</summary>
    public const string MeatFishId = "meatFish";

    public static readonly Aisle FruitVeg = new("fruitVeg", "Fruit & veg", "vegetables", "Fruit & veg");
    public static readonly Aisle Other = new("other", "Other", "shopping-cart", "Other (not food, or nothing else fits)");

    public static readonly IReadOnlyList<Aisle> All =
    [
        FruitVeg,
        new("bakery", "Bakery", "bread", "Bakery (bread, rolls, wraps, cakes)"),
        new(MeatFishId, "Meat & fish", "fish", "Meat & fish (raw)"),
        new("dairyEggs", "Dairy & eggs", "milk", "Dairy & eggs (milk, cheese, yogurt, butter, cream, eggs)"),
        new("chilledDeli", "Chilled & deli", "sandwich", "Chilled & deli (ready meals, dips, cooked meats, fresh pasta)"),
        new("frozen", "Frozen", "snowflake", "Frozen (sold frozen)"),
        new("pastaRiceGrains", "Pasta, rice & grains", "rice", "Pasta, rice & grains (dried)"),
        new("tinsJars", "Tins & jars", "canned-fruit", "Tins & jars (tinned food, jarred vegetables, spreads, honey)"),
        new("saucesOils", "Sauces, oils & condiments", "bottle-one", "Sauces, oils & condiments (ketchup, mayo, mustard, oil, vinegar, gravy)"),
        new("bakingSpices", "Baking & spices", "chili", "Baking & spices (flour, sugar, sweeteners, herbs, spices, salt, seeds, nuts for baking)"),
        new("breakfastCereal", "Breakfast & cereal", "bowl", "Breakfast & cereal (cereal, muesli, porridge, syrups)"),
        new("snacksSweets", "Snacks & sweets", "candy", "Snacks & sweets (crisps, chocolate, biscuits, bars, nuts)"),
        new("drinks", "Drinks", "tea-drink", "Drinks (tea, coffee, juice, soft drinks, alcohol, supplements)"),
        Other,
    ];

    private static readonly Dictionary<string, Aisle> ById = All.ToDictionary(a => a.Id);

    private static readonly Dictionary<string, int> Position = All.Select((a, i) => (a.Id, i)).ToDictionary();

    public static Aisle? Find(string? id) => id is not null && ById.TryGetValue(id, out var aisle) ? aisle : null;

    /// <summary>The product's aisle, or <see cref="Other"/> until it has one.</summary>
    public static Aisle Of(string? id) => Find(id) ?? Other;

    /// <summary>Position in shop order, for sorting (no aisle yet: last, with Other).</summary>
    public static int Order(string? id) => id is not null && Position.TryGetValue(id, out var i) ? i : All.Count - 1;
}
