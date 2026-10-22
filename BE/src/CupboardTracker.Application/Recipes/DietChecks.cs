using System.Text.RegularExpressions;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Recipes;

/// <summary>What a diet check found (#110): <see cref="Problems"/> are worth one retry and become "Check before
/// cooking" warnings if they're still there; <see cref="Warnings"/> are only ever warnings (unknown products).</summary>
public sealed record DietCheck(IReadOnlyList<DietProblem> Problems, IReadOnlyList<string> Warnings)
{
    public static readonly DietCheck Passed = new([], []);

    public bool Ok => Problems.Count == 0;
}

/// <summary>One thing wrong: <see cref="ForModel"/> for the retry message, <see cref="ForPeople"/> as the warning.</summary>
public sealed record DietProblem(string ForModel, string ForPeople);

/// <summary>
/// The deterministic checks behind Veggie and Half &amp; half (#110), and the one meat/fish word list. Veggie: no meat or
/// fish product, and no meat or fish word anywhere (veggie forms like "veggie sausages" or "vegetable stock" are fine).
/// Half &amp; half: meat and fish products only on the meat plates, no meat or fish word on the shared or veggie side, both
/// sides present, and the servings adding up. Products whose animal content is unknown are allowed, with a warning.
/// </summary>
public static partial class DietChecks
{
    /// <summary>The meat and fish words, as one list (lower case).</summary>
    public static readonly IReadOnlyList<string> MeatWords =
    [
        "bacon", "ham", "chorizo", "chicken", "beef", "pork", "lamb", "mince", "sausage", "sausages", "fish", "prawn", "prawns",
        "tuna", "salmon", "anchovy", "anchovies", "meat stock", "chicken stock", "beef stock", "fish stock", "gravy", "dripping",
        "lard", "worcestershire", "fish sauce",
    ];

    /// <summary>The first meat or fish word in the text, unless it's a veggie form ("veggie sausages", "vegetable stock",
    /// "meat-free mince"); null if none.</summary>
    public static string? MeatWordIn(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (Match m in MeatWordPattern().Matches(lower))
        {
            var before = lower[Math.Max(0, m.Index - 24)..m.Index];
            if (!VeggieForm().IsMatch(before))
            {
                return m.Value;
            }
        }

        return null;
    }

    /// <summary>The words that make the next word a veggie form, just before it.</summary>
    [GeneratedRegex(@"(veggie|vegetarian|vegan|vegetable|meat[\s-]?free|plant[\s-]?based|meatless|quorn|tofu|soya|mushroom)[\s-]+([a-z-]+[\s-]+)?$")]
    private static partial Regex VeggieForm();

    [GeneratedRegex(@"\b(meat stock|chicken stock|beef stock|fish stock|fish sauce|anchov(y|ies)|bacon|ham|chorizo|chicken|beef|pork|lamb|mince|sausages?|fish|prawns?|tuna|salmon|gravy|dripping|lard|worcestershire)\b")]
    private static partial Regex MeatWordPattern();

    private static string Warn(string word) => word switch
    {
        "worcestershire" => "Worcestershire sauce usually contains anchovies",
        "fish sauce" => "Fish sauce is made from fish",
        _ => $"This recipe mentions {word}",
    };

    /// <summary>A recipe against its diet. Nothing to check for <see cref="Diet.Any"/>.</summary>
    public static DietCheck Check(Recipe recipe, RecipeRequest request)
    {
        if (request.Diet == Diet.Any)
        {
            return DietCheck.Passed;
        }

        var stock = request.Stock.ToDictionary(s => s.ProductId);
        var problems = new List<DietProblem>();
        var warnings = new List<string>();
        var half = request.Diet == Diet.Half;
        bool Shared(PlateFor plate) => !half || plate != PlateFor.Meat;

        foreach (var i in recipe.Ingredients)
        {
            var content = i.ProductId is { } id && stock.TryGetValue(id, out var item) ? item.AnimalContent : null;
            if (content is { } c && AnimalContents.IsMeatOrFish(c) && Shared(i.For))
            {
                problems.Add(half
                    ? new($"{i.Name} is {AnimalContents.Id(c)} but isn't marked for the meat plates", $"{i.Name} is {AnimalContents.Id(c)}: keep it to the meat plates.")
                    : new($"{i.Name} is {AnimalContents.Id(c)}", $"{i.Name} is {AnimalContents.Id(c)}, which isn't vegetarian."));
            }
            else if (content == AnimalContent.Unknown && Shared(i.For))
            {
                warnings.Add($"Check {i.Name} is vegetarian.");
            }
            else if (content is null && Shared(i.For) && MeatWordIn(i.Name) is { } word)
            {
                problems.Add(WordProblem(word, $"the ingredient {i.Name}", half));
            }
        }

        foreach (var a in recipe.AlsoNeeded.Where(a => Shared(a.For)))
        {
            if (MeatWordIn(a.Name) is { } word)
            {
                problems.Add(WordProblem(word, $"the extra item {a.Name}", half));
            }
        }

        foreach (var s in recipe.Steps.Where(s => Shared(s.For)))
        {
            if (MeatWordIn(s.Text) is { } word)
            {
                problems.Add(WordProblem(word, half ? "a step that isn't marked for the meat plates" : "a step", half));
            }
        }

        if (half)
        {
            var meatSide = recipe.Ingredients.Any(i => i.For == PlateFor.Meat) || recipe.Steps.Any(s => s.For == PlateFor.Meat);
            var vegSide = recipe.Ingredients.Any(i => i.For != PlateFor.Meat) && recipe.Steps.Any(s => s.For != PlateFor.Meat);
            if (!meatSide || !vegSide)
            {
                problems.Add(new("half and half needs something marked for the meat plates and a way to the veggie plates",
                    "This doesn't clearly split into meat and veggie plates."));
            }

            if (recipe.ServingsMeat + recipe.ServingsVeg != recipe.Servings || recipe.ServingsMeat == 0 || recipe.ServingsVeg == 0)
            {
                problems.Add(new($"servingsMeat ({recipe.ServingsMeat}) and servingsVeg ({recipe.ServingsVeg}) must both be set and add up to servings ({recipe.Servings})",
                    "Check how many plates are meat and how many veggie."));
            }
        }

        return new DietCheck(Distinct(problems), [.. warnings.Distinct()]);
    }

    /// <summary>A Ready now idea against Veggie (ideas are never half &amp; half).</summary>
    public static DietCheck Check(QuickIdea idea, RecipeRequest request)
    {
        if (request.Diet != Diet.Veg)
        {
            return DietCheck.Passed;
        }

        var stock = request.Stock.ToDictionary(s => s.ProductId);
        var problems = new List<DietProblem>();
        var warnings = new List<string>();
        foreach (var id in idea.Uses)
        {
            if (stock.TryGetValue(id, out var item) && item.AnimalContent is { } c)
            {
                if (AnimalContents.IsMeatOrFish(c))
                {
                    problems.Add(new($"{item.Name} is {AnimalContents.Id(c)}", $"{item.Name} is {AnimalContents.Id(c)}, which isn't vegetarian."));
                }
                else if (c == AnimalContent.Unknown)
                {
                    warnings.Add($"Check {item.Name} is vegetarian.");
                }
            }
        }

        foreach (var text in new[] { idea.Title, idea.How, idea.AlsoNeeded }.OfType<string>())
        {
            if (MeatWordIn(text) is { } word)
            {
                problems.Add(WordProblem(word, "the idea", half: false));
            }
        }

        return new DietCheck(Distinct(problems), [.. warnings.Distinct()]);
    }

    private static DietProblem WordProblem(string word, string where, bool half) => half
        ? new($"{where} mentions {word}, which belongs on the meat plates only", $"{Warn(word)}: keep it to the meat plates.")
        : new($"{where} mentions {word}, which isn't vegetarian", $"{Warn(word)}, which isn't vegetarian.");

    private static List<DietProblem> Distinct(List<DietProblem> problems) => [.. problems.DistinctBy(p => p.ForPeople)];
}
