namespace CupboardTracker.Domain.Products;

/// <summary>
/// The icons a product can have (#72, spike #60): IconPark Outline's "Foods" category plus a few extras, by name. Jev
/// picks one per product; Gary can pick any of them in Edit details. The UI bundles exactly these, plus the aisle
/// icons (<c>FE/src/productIcons.json</c>, from <c>FE/scripts/build-icons.mjs</c>; a test keeps the two in step).
/// </summary>
public static class ProductIcons
{
    /// <summary>Below this, Jev's pick isn't kept: the product shows its aisle's icon instead (spike #60: every
    /// IconPark pick below 0.3 was close or wrong).</summary>
    public const double MinConfidence = 0.3;

    public static readonly IReadOnlyList<string> Names =
    [
        "apple", "apple-one", "avocado", "avocado-one", "banana", "barbecue", "beer", "beer-mug", "birthday-cake",
        "bone", "bottle", "bottle-one", "bottle-three", "bottle-two", "bowl", "bowl-one", "bread", "bread-machine",
        "bread-one", "cake", "cake-five", "cake-four", "cake-one", "cake-three", "cake-two", "candy", "canned-fruit",
        "carrot", "chafing-dish", "chafing-dish-one", "cheese", "chef-hat-one", "cherry", "chicken", "chicken-leg",
        "chili", "chopping-board", "chopsticks-fork", "cola", "cook", "cooking", "crab", "croissant", "cup",
        "cup-four", "cup-one", "doughnut", "drink", "drumstick", "egg", "egg-one", "eggplant", "fish", "fork-spoon",
        "french-fries", "garlic", "glove", "goblet", "goblet-cracking", "goblet-full", "goblet-one", "hamburger",
        "hamburger-one", "honey", "honey-one", "hot-pot", "hot-pot-one", "icecream", "icecream-five", "icecream-four",
        "icecream-one", "icecream-three", "icecream-two", "juice", "kettle-one", "kitchen-knife", "knife-fork",
        "leaves", "lemon", "liqueur", "lollipop", "macadamia-nut", "measuring-cup", "milk", "milk-one", "noodles",
        "nut", "orange", "orange-one", "oven", "oven-tray", "painted-eggshell", "peach", "pear", "peas", "pig", "pill",
        "pineapple", "popcorn", "popcorn-one", "pot", "powder", "pumpkin", "radish", "radish-one", "refrigerator",
        "rice", "sandwich", "sandwich-one", "scallion", "seedling", "shopping", "shovel", "shovel-one", "shrimp",
        "snacks", "snowflake", "soybean-milk-maker", "spoon", "tea", "tea-drink", "teapot", "thermos-cup", "tomato",
        "tray", "turkey", "vegetable-basket", "vegetables", "vicia-faba", "watermelon", "watermelon-one",
    ];

    private static readonly HashSet<string> Known = [.. Names];

    public static bool IsKnown(string? name) => name is not null && Known.Contains(name);
}
