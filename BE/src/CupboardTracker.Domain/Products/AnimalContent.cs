using System.Text.RegularExpressions;

namespace CupboardTracker.Domain.Products;

/// <summary>
/// Whether a product is or contains meat or fish, or is made from them (#112): stock cubes, meat gravy, lard,
/// gelatine, anchovy sauces count; cheese, rennet, eggs, milk and honey don't. Fish counts as meat for "veg", but the
/// flag records which. <see cref="Unknown"/>: Jev wasn't sure enough.
/// </summary>
public enum AnimalContent
{
    Meat,
    Fish,
    None,
    Unknown,
}

/// <summary>A product's animal content and where it came from.</summary>
public sealed record ProductAnimalContent(AnimalContent Value, string Source, double? Confidence, DateTimeOffset At)
{
    public bool IsManual => Source == AnimalContents.ManualSource;
}

/// <summary>Ids and sources as stored, and the Open Food Facts category rule (#112).</summary>
public static partial class AnimalContents
{
    public const string AisleSource = "aisle";
    public const string OpenFoodFactsSource = "openFoodFacts";
    public const string JevSource = "jev";
    public const string ManualSource = "manual";

    /// <summary>Below this, Jev's "meat / fish / neither" answer is stored as <see cref="AnimalContent.Unknown"/>.</summary>
    public const double MinConfidence = 0.6;

    public static string Id(AnimalContent value) => value.ToString().ToLowerInvariant();

    public static AnimalContent? Parse(string? id) => id switch
    {
        "meat" => AnimalContent.Meat,
        "fish" => AnimalContent.Fish,
        "none" => AnimalContent.None,
        "unknown" => AnimalContent.Unknown,
        _ => null,
    };

    /// <summary>Meat or fish: never on a veg plate (#110).</summary>
    public static bool IsMeatOrFish(AnimalContent value) => value is AnimalContent.Meat or AnimalContent.Fish;

    /// <summary>
    /// What the cached Open Food Facts categories say, if they're decisive: a vegetarian, vegan or meat-free category →
    /// none; a fish or seafood category → fish; a meat category → meat. Otherwise null (Jev decides). Only categories
    /// are cached; labels and ingredients aren't fetched.
    /// </summary>
    public static AnimalContent? FromCategoryTags(IEnumerable<string> tags)
    {
        var list = tags.Select(t => t.Trim().ToLowerInvariant()).ToList();
        if (list.Any(t => VegTags().IsMatch(t)))
        {
            return AnimalContent.None;
        }

        if (list.Any(t => FishTags().IsMatch(t)))
        {
            return AnimalContent.Fish;
        }

        return list.Any(t => MeatTags().IsMatch(t)) ? AnimalContent.Meat : null;
    }

    [GeneratedRegex(@"^en:([a-z-]*-)?(vegetarian|vegan|meat-analogues|meat-alternatives|meat-substitutes)([a-z-]*)?$")]
    private static partial Regex VegTags();

    [GeneratedRegex(@"^en:([a-z-]*-)?(fishes|seafood|tunas|salmons|sardines|mackerels|anchovies|prawns|shrimps|crustaceans|mussels|fish-fillets)$")]
    private static partial Regex FishTags();

    [GeneratedRegex(@"^en:(([a-z-]*-)?(meats|poultries|hams|bacons|sausages|salamis|chorizos|lards)|chickens|beef|pork|lamb|minced-meats|chicken-breasts)$")]
    private static partial Regex MeatTags();
}
