using System.Globalization;
using System.Text.RegularExpressions;

namespace CupboardTracker.Domain.Products;

/// <summary>
/// A generic name (#90): what you'd say to someone going to the shop ("Semi-skimmed milk"), shared by every product
/// of that kind whatever the brand. One record per name, in the aisle of the product that created it.
/// </summary>
public sealed record GenericName(string Id, string Name, string AisleId, DateTimeOffset CreatedAt, string CreatedBy)
{
    public const string FoundrySource = "foundry";

    public static GenericName Create(string name, string aisleId, DateTimeOffset at, string createdBy)
    {
        var display = GenericNames.Display(name);
        return new GenericName(GenericNames.IdOf(display), display, aisleId, at, createdBy);
    }
}

/// <summary>Where a product's generic name came from (#90): matched by Jev, written by Foundry, or chosen by hand.</summary>
public enum GenericNameSource
{
    JevMatch,
    Foundry,
    Manual,
    Fallback,
}

/// <summary>A product's generic name (#90): the record's id and display name, how it was chosen, and when.</summary>
public sealed record ProductGenericName(string Id, string Name, GenericNameSource Source, double? Confidence, DateTimeOffset At)
{
    public bool IsManual => Source == GenericNameSource.Manual;
}

/// <summary>Rules for generic names (#90): normalising, display form, validation, and the words used to match.</summary>
public static partial class GenericNames
{
    public const int MaxLength = 40;

    /// <summary>How a source is written in documents and the API: jevMatch, foundry, manual, fallback.</summary>
    public static string SourceName(GenericNameSource source) => source switch
    {
        GenericNameSource.JevMatch => "jevMatch",
        GenericNameSource.Foundry => "foundry",
        GenericNameSource.Manual => "manual",
        _ => "fallback",
    };

    /// <summary>The record id: lower case, trimmed, single spaces (UK spelling as written).</summary>
    public static string IdOf(string name) => Product.NameKeyOf(name);

    /// <summary>Sentence case with single spaces: the first letter upper case, the rest as given (keeps "Dijon").</summary>
    public static string Display(string name)
    {
        var text = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
    }

    /// <summary>Why a name can't be used, or null: 1–40 characters, no size or weight, not the brand.</summary>
    public static string? Problem(string? name, string? brand = null)
    {
        var text = name is null ? "" : Display(name);
        if (text.Length is 0 or > MaxLength)
        {
            return $"A generic name is 1 to {MaxLength} characters.";
        }

        if (Size().IsMatch(text))
        {
            return "A generic name has no size or weight.";
        }

        if (!string.IsNullOrWhiteSpace(brand) && brand.Trim().Length > 2
            && text.Contains(brand.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "A generic name has no brand.";
        }

        return null;
    }

    /// <summary>The product's name with sizes, counts and pack words stripped: the fallback when nothing else works.</summary>
    public static string FallbackFrom(string displayName)
    {
        var stripped = Display(Spaces().Replace(Size().Replace(displayName, " "), " ").Trim(' ', '-', ',', '.'));
        if (stripped.Length > MaxLength)
        {
            stripped = stripped[..MaxLength].TrimEnd();
        }

        return stripped.Length == 0 ? Display(displayName.Length > MaxLength ? displayName[..MaxLength] : displayName) : stripped;
    }

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "of", "with", "in", "for", "x", "pack", "packs", "multipack", "pot", "pots", "tin", "tins",
        "jar", "bag", "bottle", "box", "original", "classic", "authentic", "british", "deluxe", "finest", "taste", "style",
        "rich", "fresh", "free", "range", "organic", "natural", "premium", "extra", "new", "flavour", "flavoured",
    };

    /// <summary>The meaningful words of a name for the word-matching retry: lower case, no brand words, sizes or
    /// filler, simple plurals folded ("tomatoes" → "tomato").</summary>
    public static IReadOnlySet<string> Words(string text, string? brand = null)
    {
        var brandWords = NotWord().Split(brand ?? "").Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToHashSet();
        return NotWord().Split(Size().Replace(text, " "))
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length > 2 && !w.Any(char.IsDigit) && !Filler.Contains(w) && !brandWords.Contains(w))
            .Select(Singular)
            .ToHashSet();
    }

    private static string Singular(string word) =>
        word.EndsWith("oes", StringComparison.Ordinal) ? word[..^2]
        : word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4 ? word[..^3] + "y"
        : word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && word.Length > 3 ? word[..^1]
        : word;

    /// <summary>A size, weight or count with its unit ("500 g", "2 x 15 ml", "4 pints", "6 pack", "x4"); a bare
    /// number ("Chinese 5 spice") isn't one.</summary>
    [GeneratedRegex(@"\b\d+(?:[.,]\d+)?\s*(?:x\s*\d+(?:[.,]\d+)?\s*)?(?:g|kg|mg|ml|l|ltr|litres?|cl|oz|lb|pints?|pt|pack|pk|tablets|bags|slices)\b|\b\d+\s*x\b|\bx\s*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex Size();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[^\p{L}]+")]
    private static partial Regex NotWord();
}
