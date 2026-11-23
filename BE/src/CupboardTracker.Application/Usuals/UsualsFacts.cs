using CupboardTracker.Application.ShoppingList;

namespace CupboardTracker.Application.Usuals;

/// <summary>
/// The facts behind "Add usuals" (#134, spike #135): check-ins counted by <b>Monday-to-Sunday week</b> (UK time) since the stock take, the
/// current unfinished week left out. A candidate is a name bought in two or more weeks. Stock is not an input: the spike measured it and
/// it made the suggestions worse. Also the plain rule that stands in when Jev is off or fails.
/// </summary>
public static class UsualsFacts
{
    public static IReadOnlyList<UsualsCandidate> Candidates(
        IEnumerable<UsualsCheckIn> checkIns, DateTimeOffset historyStart, DateTimeOffset now, TimeZoneInfo zone, int minWeeks = 2)
    {
        var first = MondayOf(historyStart, zone);
        var current = MondayOf(now, zone);
        var weeks = new List<DateOnly>();
        for (var monday = first; monday < current; monday = monday.AddDays(7))
        {
            weeks.Add(monday);
        }

        if (weeks.Count < minWeeks)
        {
            return [];
        }

        var byKey = new Dictionary<string, List<UsualsCheckIn>>(StringComparer.Ordinal);
        foreach (var checkIn in checkIns)
        {
            if (checkIn.CheckedInAt < historyStart || MondayOf(checkIn.CheckedInAt, zone) >= current)
            {
                continue;
            }

            var key = ShoppingListNames.Normalize(checkIn.Key);
            if (key.Length == 0)
            {
                continue;
            }

            if (!byKey.TryGetValue(key, out var list))
            {
                byKey[key] = list = [];
            }

            list.Add(checkIn);
        }

        var candidates = new List<UsualsCandidate>();
        foreach (var (key, list) in byKey)
        {
            var bought = new bool[weeks.Count];
            foreach (var checkIn in list)
            {
                var index = (MondayOf(checkIn.CheckedInAt, zone).DayNumber - first.DayNumber) / 7;
                if (index >= 0 && index < bought.Length)
                {
                    bought[index] = true;
                }
            }

            if (bought.Count(b => b) < minWeeks)
            {
                continue;
            }

            var latest = list.MaxBy(c => c.CheckedInAt)!;
            candidates.Add(new UsualsCandidate(key, latest.Name, latest.Aisle, latest.ProductId, latest.GenericNameId, bought));
        }

        return [.. candidates.OrderBy(c => c.Key, StringComparer.Ordinal)];
    }

    /// <summary>"Bought in 4 of the last 4 weeks": the line under a suggestion, from the facts alone.</summary>
    public static string Why(UsualsCandidate c) =>
        c.WeeksBought == c.Weeks.Count
            ? $"Bought in {c.WeeksBought} of the last {c.Weeks.Count} weeks"
            : $"Bought in {c.WeeksBought} of the last {c.Weeks.Count} weeks" + (c.WeeksSinceLast == 0 ? ", including last week" : "");

    /// <summary>The share of weeks it was bought in, 0 to 1: the plain rule's score.</summary>
    public static double Share(UsualsCandidate c) => c.Weeks.Count == 0 ? 0 : (double)c.WeeksBought / c.Weeks.Count;

    public static IReadOnlyList<UsualsItem> ByPattern(IReadOnlyList<UsualsCandidate> candidates, UsualsSettings settings) =>
        Order(candidates.Select(c => (Candidate: c, Score: Share(c))), settings.PatternDue, settings.PatternMaybe);

    public static IReadOnlyList<UsualsItem> ByJev(IReadOnlyList<UsualsCandidate> candidates, IReadOnlyList<double> probabilities, UsualsSettings settings) =>
        Order(candidates.Select((c, i) => (Candidate: c, Score: probabilities[i])), settings.DueConfidence, settings.MaybeConfidence);

    /// <summary>Every candidate at or above the Maybe cut-off, Due before Maybe, best first. <b>Not capped</b> (Gary): the number
    /// offered is however many clear the cut-off.</summary>
    private static IReadOnlyList<UsualsItem> Order(IEnumerable<(UsualsCandidate Candidate, double Score)> scored, double due, double maybe) =>
        [.. scored
            .Where(x => x.Score >= maybe)
            .OrderByDescending(x => x.Score >= due)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.Key, StringComparer.Ordinal)
            .Select(x => new UsualsItem(
                x.Candidate.Name, x.Candidate.GenericNameId, x.Candidate.Aisle, x.Score >= due ? UsualsBand.Due : UsualsBand.Maybe,
                Why(x.Candidate), x.Candidate.ProductId))];

    /// <summary>The Monday (in <paramref name="zone"/>) of the week the instant falls in.</summary>
    public static DateOnly MondayOf(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        return local.AddDays(-(((int)local.DayOfWeek + 6) % 7));
    }
}
