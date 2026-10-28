using System.Text.RegularExpressions;

namespace CupboardTracker.Application.Plans;

/// <summary>A stock product as planning sees it: what the allocation rule needs (spike #124).</summary>
public sealed record PlanStockItem(Guid ProductId, string Name, string GenericName, int Quantity, string AisleId, bool UseFirst);

/// <summary>
/// Stock allocation (spike #124): who gets what is in stock is decided in code, with no quantities beyond units. One unit of a
/// product serves ONE meal, except bulk products, which serve any number. Meals claim in plan order, kept meals first. A claim
/// beyond capacity is short: that meal needs another one, so its name joins what's to buy.
/// </summary>
public static partial class StockAllocation
{
    /// <summary>The aisles where one pack lasts many meals.</summary>
    public static readonly IReadOnlySet<string> BulkAisles =
        new HashSet<string> { "pastaRiceGrains", "bakingSpices", "saucesOils", "breakfastCereal", "drinks" };

    public static bool IsBulk(PlanStockItem item) => BulkAisles.Contains(item.AisleId) || BulkWords().IsMatch(item.Name);

    /// <summary>The generic names each meal is short of, by meal id; and the claims that named no product in stock.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Shortfalls(
        IReadOnlyList<PlannedMeal> meals, IReadOnlyDictionary<Guid, PlanStockItem> stock)
    {
        var used = new Dictionary<Guid, int>();
        var shortBy = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var meal in meals.OrderBy(m => !m.Keep)) // stable: kept first, then plan order
        {
            var missing = new List<string>();
            foreach (var product in meal.Uses)
            {
                if (!stock.TryGetValue(product.ProductId, out var item))
                {
                    continue;
                }

                used.TryGetValue(item.ProductId, out var n);
                if (IsBulk(item) || n < item.Quantity)
                {
                    used[item.ProductId] = n + 1;
                }
                else
                {
                    missing.Add(item.GenericName);
                }
            }

            if (missing.Count > 0)
            {
                shortBy[meal.Id] = missing;
            }
        }

        return shortBy;
    }

    [GeneratedRegex(@"\b(salt|pepper|oil|flour|sugar|rice|pasta|spice|spices|stock cube|vinegar)\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BulkWords();
}

/// <summary>Overlap (spike #124), measured in code: a thing to buy needed by exactly one meal is an orphan; by two or more, shared.</summary>
public static class PlanOverlap
{
    /// <summary>Lower case, without filler words and a plural s, so "Peppers" and "pepper" are one thing.</summary>
    public static string Normalise(string name)
    {
        var n = Regex.Replace(name.Trim().ToLowerInvariant(), @"\b(fresh|large|small|ripe|tinned|dried)\b", "", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        return n.Length > 4 ? Regex.Replace(n, "(es|s)$", "", RegexOptions.None, TimeSpan.FromSeconds(1)) : n;
    }

    /// <summary>Each normalised thing to buy and the ids of the meals that need it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Needs(IEnumerable<PlannedMeal> meals)
    {
        var needs = new Dictionary<string, HashSet<string>>();
        foreach (var meal in meals)
        {
            foreach (var name in meal.ToBuy)
            {
                var key = Normalise(name);
                if (!needs.TryGetValue(key, out var ids))
                {
                    needs[key] = ids = [];
                }

                ids.Add(meal.Id);
            }
        }

        return needs.ToDictionary(n => n.Key, n => (IReadOnlySet<string>)n.Value);
    }

    /// <summary>Things bought for only one meal (name as the meal wrote it), with that meal's id.</summary>
    public static IReadOnlyList<(string Name, string MealId)> Orphans(IReadOnlyList<PlannedMeal> meals)
    {
        var needs = Needs(meals);
        return [.. meals.SelectMany(m => m.ToBuy.Select(b => (Name: b, MealId: m.Id)))
            .Where(x => needs[Normalise(x.Name)].Count == 1).DistinctBy(x => Normalise(x.Name))];
    }

    /// <summary>What this meal buys that exactly one other meal buys too: removing or replacing it would strand them.</summary>
    public static IReadOnlyList<string> Stranded(IReadOnlyList<PlannedMeal> meals, string mealId)
    {
        var needs = Needs(meals);
        var mine = meals.First(m => m.Id == mealId).ToBuy;
        return [.. mine.Where(b => needs[Normalise(b)].Count(id => id != mealId) == 1).DistinctBy(Normalise)];
    }

    /// <summary>For each meal: what it shares with others, and with how many other meals.</summary>
    public static MealShares? SharesOf(IReadOnlyList<PlannedMeal> meals, PlannedMeal meal)
    {
        var needs = Needs(meals);
        var shared = meal.ToBuy.Where(b => needs[Normalise(b)].Count >= 2).DistinctBy(Normalise).ToList();
        if (shared.Count == 0)
        {
            return null;
        }

        var others = shared.SelectMany(b => needs[Normalise(b)]).Where(id => id != meal.Id).Distinct().Count();
        return new MealShares(shared, others);
    }
}

/// <summary>"Another one" and "Add a meal" can't vary through temperature (luna takes only its default), so code does: a
/// different angle each time (spike #124: 4 of 4 distinct dishes).</summary>
public static class PlanAngles
{
    public static readonly IReadOnlyList<string> All =
    [
        "a rice dish", "a pasta dish", "a soup or stew", "a noodle dish", "a wrap, taco or sandwich", "a curry",
        "a bake or roast", "a salad or bowl", "an egg dish", "a one-pan dish",
    ];

    public static string Pick(Random random) => All[random.Next(All.Count)];
}

/// <summary>One product or generic name with how often it was bought lately.</summary>
public sealed record PurchaseUnit(string Key, DateTimeOffset CheckedInAt, DateTimeOffset? CheckedOutAt);

/// <summary>A name ever put on the shopping list, kept for good (the list's memory), with how often.</summary>
public sealed record ListMemoryEntry(string Name, int TimesAdded, string? AisleId);

public sealed record UsuallyBuysSettings(DateTimeOffset Start, int MinScore = 2, int TopN = 30)
{
    /// <summary>The day after the stock take (18 Oct 2026 was the stock take, 19 Oct the first shop, spike #124).</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);

    public static UsuallyBuysSettings Default { get; } = new(DefaultStart);
}

/// <summary>
/// "Usually buys" (spike #124): names scored from what's been checked in on different days since the stock take (capped at 4),
/// what's been put on the shopping list (capped at 3) and whether one was used up (1). At least <see cref="UsuallyBuysSettings.MinScore"/>
/// to count, best first, at most <see cref="UsuallyBuysSettings.TopN"/>. Names in the Other aisle (kitchen foil) are left out. Empty
/// history gives an empty list, and the plan is made without it.
/// </summary>
public static class UsuallyBuys
{
    public static IReadOnlyList<string> Rank(
        IEnumerable<PurchaseUnit> units, IEnumerable<ListMemoryEntry> memory, UsuallyBuysSettings settings)
    {
        var trips = new Dictionary<string, HashSet<DateOnly>>();
        var used = new HashSet<string>();
        foreach (var unit in units)
        {
            var key = unit.Key.Trim().ToLowerInvariant();
            if (key.Length == 0)
            {
                continue;
            }

            if (unit.CheckedInAt >= settings.Start)
            {
                if (!trips.TryGetValue(key, out var days))
                {
                    trips[key] = days = [];
                }

                days.Add(DateOnly.FromDateTime(unit.CheckedInAt.UtcDateTime));
            }

            if (unit.CheckedOutAt is { } out_ && out_ >= settings.Start)
            {
                used.Add(key);
            }
        }

        var listed = memory.Where(m => m.AisleId != "other").GroupBy(m => m.Name.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => Math.Min(g.Sum(m => Math.Max(m.TimesAdded, 1)), 3));
        return [.. trips.Keys.Concat(used).Concat(listed.Keys).Distinct()
            .Select(k => (Key: k, Score: Math.Min(trips.GetValueOrDefault(k)?.Count ?? 0, 4) + listed.GetValueOrDefault(k) + (used.Contains(k) ? 1 : 0)))
            .Where(x => x.Score >= settings.MinScore)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(settings.TopN).Select(x => x.Key)];
    }
}
