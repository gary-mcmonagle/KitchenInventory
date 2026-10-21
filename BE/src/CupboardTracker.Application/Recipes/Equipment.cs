using System.Text.RegularExpressions;

namespace CupboardTracker.Application.Recipes;

/// <summary>The kitchen kit a Cook something recipe may use (#111). The kettle is always allowed and isn't one of these.</summary>
public enum Kit
{
    Hob,
    OvenGrill,
    Microwave,
    Toaster,
    AirFryer,
    SlowCooker,
    RiceCooker,
    SoupMaker,
    Blender,
}

/// <summary>
/// The equipment chips (#111): ids as the API and schema use them, labels as the app shows them, and the words that
/// give a method step away as using a piece of kit (the deterministic check of the steps). Kept here, in one place.
/// </summary>
public static partial class Kits
{
    public static readonly IReadOnlySet<Kit> All = new HashSet<Kit>(Enum.GetValues<Kit>());

    public static string Id(Kit kit) => kit switch
    {
        Kit.Hob => "hob",
        Kit.OvenGrill => "ovenGrill",
        Kit.Microwave => "microwave",
        Kit.Toaster => "toaster",
        Kit.AirFryer => "airFryer",
        Kit.SlowCooker => "slowCooker",
        Kit.RiceCooker => "riceCooker",
        Kit.SoupMaker => "soupMaker",
        _ => "blender",
    };

    public static Kit? Parse(string? id) => Enum.GetValues<Kit>().Cast<Kit?>().FirstOrDefault(k => Id(k!.Value) == id);

    /// <summary>As on the chips: "Oven & grill".</summary>
    public static string Label(Kit kit) => kit switch
    {
        Kit.Hob => "Hob",
        Kit.OvenGrill => "Oven & grill",
        Kit.Microwave => "Microwave",
        Kit.Toaster => "Toaster",
        Kit.AirFryer => "Air fryer",
        Kit.SlowCooker => "Slow cooker",
        Kit.RiceCooker => "Rice cooker",
        Kit.SoupMaker => "Soup maker",
        _ => "Blender",
    };

    /// <summary>In a sentence: "the oven or grill".</summary>
    public static string InProse(Kit kit) => kit switch
    {
        Kit.OvenGrill => "the oven or grill",
        Kit.Blender => "a blender",
        _ => "the " + Label(kit).ToLowerInvariant(),
    };

    /// <summary>"This recipe mentions the oven or grill, which you said you can't use."</summary>
    public static string Warning(Kit kit) => $"This recipe mentions {InProse(kit)}, which you said you can't use.";

    /// <summary>
    /// Whether a method step uses a piece of kit. Adjectives on ingredients don't count ("grilled peppers", "baked
    /// beans", "roasted nuts"), boiling with a kettle isn't the hob, and an air fryer's temperature or preheat isn't the
    /// oven's.
    /// </summary>
    public static bool Mentions(string step, Kit kit)
    {
        var text = step.ToLowerInvariant();
        var airFryer = AirFryerWords().IsMatch(text);
        return kit switch
        {
            Kit.Hob => HobWords().IsMatch(AirFryerWords().Replace(text, " "))
                || (!text.Contains("kettle", StringComparison.Ordinal) && BoilWords().IsMatch(text)),
            Kit.OvenGrill => OvenWords().IsMatch(text.Replace("baking powder", " ", StringComparison.Ordinal).Replace("baking soda", " ", StringComparison.Ordinal))
                || (!airFryer && OvenHeatWords().IsMatch(text)),
            Kit.Microwave => text.Contains("microwave", StringComparison.Ordinal),
            Kit.Toaster => text.Contains("toaster", StringComparison.Ordinal),
            Kit.AirFryer => airFryer,
            Kit.SlowCooker => SlowCookerWords().IsMatch(text),
            Kit.RiceCooker => text.Contains("rice cooker", StringComparison.Ordinal),
            Kit.SoupMaker => text.Contains("soup maker", StringComparison.Ordinal),
            _ => BlenderWords().IsMatch(text),
        };
    }

    /// <summary>The kit a recipe's steps (and declared equipment) use that isn't allowed, each once.</summary>
    public static IReadOnlyList<Kit> NotAllowed(IEnumerable<string> steps, IEnumerable<Kit> declared, IReadOnlySet<Kit> allowed)
    {
        var list = steps.ToList();
        return [.. Enum.GetValues<Kit>().Where(k => !allowed.Contains(k) && (declared.Contains(k) || list.Any(s => Mentions(s, k))))];
    }

    [GeneratedRegex(@"\bair[\s-]?fr(y|yer|yers|ied|ying)\b")]
    private static partial Regex AirFryerWords();

    [GeneratedRegex(@"\b(hob|saucepan|frying pan|pan|wok|simmer|simmering|fry|frying|sauté|saute|sautéing|stir[\s-]?fry|poach)\b")]
    private static partial Regex HobWords();

    [GeneratedRegex(@"\b(boil|boiling|bring to the boil)\b")]
    private static partial Regex BoilWords();

    [GeneratedRegex(@"\b(oven|grill|grilling|roast|roasting|bake|baking|gas mark)\b")]
    private static partial Regex OvenWords();

    [GeneratedRegex(@"(\bpreheat|\d\s*°\s*c\b|°c)")]
    private static partial Regex OvenHeatWords();

    [GeneratedRegex(@"\b(slow[\s-]?cooker|crock[\s-]?pot)\b")]
    private static partial Regex SlowCookerWords();

    [GeneratedRegex(@"\b(blender|blend|blitz|food processor)\b")]
    private static partial Regex BlenderWords();
}
