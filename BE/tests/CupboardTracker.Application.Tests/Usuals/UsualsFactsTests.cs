using CupboardTracker.Application.Usuals;

namespace CupboardTracker.Application.Tests.Usuals;

public sealed class UsualsFactsTests
{
    private static readonly TimeZoneInfo London = TestSettings.London;
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero); // a Monday
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero); // the Monday after the fifth complete week

    private static UsualsCheckIn In(string key, int month, int day, int hour = 12, string? name = null, string aisle = "dairyEggs", Guid? product = null) =>
        new(key, name ?? key, aisle, product ?? Guid.NewGuid(), null, new DateTimeOffset(2026, month, day, hour, 0, 0, TimeSpan.Zero));

    private static IReadOnlyList<UsualsCandidate> Candidates(params UsualsCheckIn[] checkIns) =>
        UsualsFacts.Candidates(checkIns, Start, Now, London);

    [Fact]
    public void Counts_each_name_by_the_weeks_it_was_bought_in_oldest_first_and_leaves_the_current_week_out()
    {
        var c = Assert.Single(Candidates(
            In("Milk", 10, 20), In("Milk", 10, 22), // week of 19 Oct: twice, one week
            In("Milk", 11, 3), // week of 2 Nov
            In("Milk", 11, 17), // week of 16 Nov
            In("Milk", 11, 23))); // this week: not history

        Assert.Equal([true, false, true, false, true], c.Weeks);
        Assert.Equal((3, 0), (c.WeeksBought, c.WeeksSinceLast));
    }

    [Fact]
    public void A_name_bought_in_one_week_only_or_before_the_stock_take_is_no_candidate()
    {
        var result = Candidates(In("Oats", 10, 20), In("Oats", 10, 21), In("Rice", 10, 12), In("Rice", 10, 20), In("Pasta", 11, 4), In("Pasta", 11, 18));

        Assert.Equal(["pasta"], result.Select(c => c.Key));
    }

    [Fact]
    public void Names_that_differ_only_in_case_and_spaces_are_one_item_and_the_latest_check_in_gives_the_name_aisle_and_product()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var c = Assert.Single(Candidates(
            In("semi-skimmed  milk", 10, 20, name: "semi-skimmed milk", aisle: "other", product: older),
            In("Semi-skimmed milk", 11, 10, name: "Semi-skimmed milk", aisle: "dairyEggs", product: newer)));

        Assert.Equal(("semi-skimmed milk", "Semi-skimmed milk", "dairyEggs", newer), (c.Key, c.Name, c.Aisle, c.ProductId));
    }

    [Fact]
    public void Weeks_run_Monday_to_Sunday_in_UK_time()
    {
        // 23:30 UTC on Sunday 8 Nov is still Sunday (GMT); 00:10 UTC on Monday 9 Nov is the new week.
        var c = Assert.Single(Candidates(In("Eggs", 11, 2, 9), In("Eggs", 11, 8, 23), In("Eggs", 11, 9, 0)));

        Assert.Equal([false, false, true, true, false], c.Weeks);
    }

    [Fact]
    public void With_fewer_than_two_complete_weeks_there_is_nothing_to_offer()
    {
        var early = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero); // one complete week since 19 Oct

        Assert.Empty(UsualsFacts.Candidates([In("Milk", 10, 20), In("Milk", 10, 27)], Start, early, London));
    }

    [Fact]
    public void The_why_line_comes_from_the_facts()
    {
        var all = new UsualsCandidate("a", "A", "other", Guid.Empty, null, [true, true, true, true]);
        var some = new UsualsCandidate("b", "B", "other", Guid.Empty, null, [true, false, true, true]);
        var older = new UsualsCandidate("c", "C", "other", Guid.Empty, null, [true, true, false, false]);

        Assert.Equal("Bought in 4 of the last 4 weeks", UsualsFacts.Why(all));
        Assert.Equal("Bought in 3 of the last 4 weeks, including last week", UsualsFacts.Why(some));
        Assert.Equal("Bought in 2 of the last 4 weeks", UsualsFacts.Why(older));
    }

    [Fact]
    public void The_plain_rule_is_due_at_three_quarters_of_the_weeks_and_maybe_from_half()
    {
        var weekly = new UsualsCandidate("weekly", "Weekly", "other", Guid.Empty, null, [true, true, true, true]);
        var mostly = new UsualsCandidate("mostly", "Mostly", "other", Guid.Empty, null, [true, true, true, false]);
        var half = new UsualsCandidate("half", "Half", "other", Guid.Empty, null, [true, false, true, false]);
        var rare = new UsualsCandidate("rare", "Rare", "other", Guid.Empty, null, [true, false, false, true, false]);

        var items = UsualsFacts.ByPattern([rare, half, mostly, weekly], new UsualsSettings());

        Assert.Equal([("Mostly", UsualsBand.Due), ("Weekly", UsualsBand.Due), ("Half", UsualsBand.Maybe)], items.Select(i => (i.Name, i.Band)).OrderBy(x => x.Item2).ThenBy(x => x.Item1));
        Assert.DoesNotContain(items, i => i.Name == "Rare");
    }

    [Fact]
    public void Jev_probabilities_split_into_due_and_maybe_with_due_first_and_the_rest_left_out()
    {
        var names = new[] { "a", "b", "c", "d", "e" };
        var candidates = names.Select(n => new UsualsCandidate(n, n.ToUpperInvariant(), "other", Guid.Empty, null, [true, true])).ToList();

        var items = UsualsFacts.ByJev(candidates, [0.95, 0.65, 0.8, 0.59, 0.7], new UsualsSettings());

        Assert.Equal([("A", UsualsBand.Due), ("C", UsualsBand.Due), ("E", UsualsBand.Maybe), ("B", UsualsBand.Maybe)], items.Select(i => (i.Name, i.Band)));
    }

    [Fact]
    public void The_selection_is_not_capped()
    {
        var many = Enumerable.Range(0, 250).Select(i => new UsualsCandidate($"item {i:000}", $"Item {i:000}", "other", Guid.Empty, null, [true, true, true])).ToList();

        Assert.Equal(250, UsualsFacts.ByJev(many, Enumerable.Repeat(0.9, 250).ToList(), new UsualsSettings()).Count);
        Assert.Equal(250, UsualsFacts.ByPattern(many, new UsualsSettings()).Count);
    }
}
