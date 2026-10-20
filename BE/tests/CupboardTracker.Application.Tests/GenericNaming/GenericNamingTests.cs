using CupboardTracker.Application.GenericNaming;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Tests.GenericNaming;

internal sealed class FakeGenericNames : IGenericNameRepository
{
    public Dictionary<string, GenericName> Items { get; } = [];

    public void Add(string name, string aisle) => Items[GenericNames.IdOf(name)] = GenericName.Create(name, aisle, DateTimeOffset.UnixEpoch, "foundry");

    public Task<IReadOnlyList<GenericName>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GenericName>>([.. Items.Values]);

    public bool Fail { get; set; }

    public Task<GenericName?> GetAsync(string id, CancellationToken cancellationToken) =>
        Fail ? throw new InvalidOperationException("Cosmos is down") : Task.FromResult(Items.GetValueOrDefault(id));

    public Task<GenericName> GetOrCreateAsync(GenericName name, CancellationToken cancellationToken)
    {
        if (!Items.TryGetValue(name.Id, out var existing))
        {
            Items[name.Id] = existing = name;
        }

        return Task.FromResult(existing);
    }

    public Task<GenericName?> SetAddWhenOutAsync(string id, bool enabled, Guid by, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (!Items.TryGetValue(id, out var name))
        {
            return Task.FromResult<GenericName?>(null);
        }

        Items[id] = name = name with { AddWhenOut = enabled, AddWhenOutBy = by, AddWhenOutAt = at };
        return Task.FromResult<GenericName?>(name);
    }
}

/// <summary>Jev, scripted: <see cref="Answer"/> decides per call from the options offered.</summary>
internal sealed class FakeMatcher : IGenericNameMatcher
{
    public List<IReadOnlyList<GenericName>> Calls { get; } = [];

    public Func<IReadOnlyList<GenericName>, GenericMatchResult> Answer { get; set; } = _ => new GenericMatchResult.NoMatch();

    public Task<GenericMatchResult> MatchAsync(ProductDescription product, IReadOnlyList<GenericName> options, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add(options);
        }

        return Task.FromResult(Answer(options));
    }

    /// <summary>Picks the option with this name when it's offered, with this confidence.</summary>
    public static Func<IReadOnlyList<GenericName>, GenericMatchResult> Picks(string name, double confidence = 0.97) =>
        options => options.FirstOrDefault(o => o.Name == name) is { } o
            ? new GenericMatchResult.Matched(o, confidence)
            : new GenericMatchResult.NoMatch();
}

internal sealed class FakeWriter : IGenericNameWriter
{
    public List<(ProductDescription Product, string Aisle, IReadOnlyList<string> Examples)> Calls { get; } = [];

    public Func<ProductDescription, GenericWriteResult> Answer { get; set; } = p => new GenericWriteResult.Written(p.Name);

    public Task<GenericWriteResult> WriteAsync(ProductDescription product, string aisleName, IReadOnlyList<string> examples, CancellationToken cancellationToken)
    {
        Calls.Add((product, aisleName, examples));
        return Task.FromResult(Answer(product));
    }
}

public sealed class GenericNamingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 21, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FakeProducts products = new();
    private readonly FakeGenericNames names = new();
    private readonly FakeMatcher matcher = new();
    private readonly FakeWriter writer = new();
    private readonly FixedClock clock = new(Now);

    private AssignGenericNames UseCase(int batchSize = GenericNameSettings.DefaultBatchSize) =>
        new(products, names, matcher, writer, new GenericNameSettings(BatchSize: batchSize), clock, new ListLogger<AssignGenericNames>());

    private Guid Known(string name, string aisle, string? brand = null, ProductGenericName? generic = null)
    {
        var id = Guid.NewGuid();
        products.Products[id] = Product.Found(id, "5000000000000", new ProductDetails(name, brand, null, null, []), Now, 1)
            with { Aisle = new ProductAisle(aisle, "jev", 0.9, Now), Generic = generic };
        return id;
    }

    private ProductGenericName? GenericOf(Guid id) => products.Products[id].Generic;

    [Fact]
    public async Task Jev_is_asked_only_the_aisles_names_and_a_confident_pick_is_used()
    {
        names.Add("Semi-skimmed milk", "dairyEggs");
        names.Add("Mature cheddar", "dairyEggs");
        names.Add("Bran flakes", "breakfastCereal");
        var milk = Known("SEMI-SKIMMED BRITISH MILK", "dairyEggs");
        matcher.Answer = FakeMatcher.Picks("Semi-skimmed milk", 0.98);

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(["Mature cheddar", "Semi-skimmed milk"], Assert.Single(matcher.Calls).Select(n => n.Name));
        Assert.Equal(("semi-skimmed milk", GenericNameSource.JevMatch, 0.98), (GenericOf(milk)!.Id, GenericOf(milk)!.Source, GenericOf(milk)!.Confidence));
        Assert.Equal(1, run.InAisle);
        Assert.Empty(writer.Calls); // no Foundry call
    }

    [Fact]
    public async Task A_pick_below_the_threshold_doesnt_count()
    {
        names.Add("Ground coffee", "drinks");
        var bags = Known("Rich Italian Coffee Bags", "drinks");
        matcher.Answer = FakeMatcher.Picks("Ground coffee", 0.84);
        writer.Answer = _ => new GenericWriteResult.Written("Coffee bags");

        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(("coffee bags", GenericNameSource.Foundry), (GenericOf(bags)!.Id, GenericOf(bags)!.Source));
    }

    [Fact]
    public async Task An_aisle_with_no_names_skips_straight_to_generating()
    {
        var leeks = Known("leek", "fruitVeg");
        writer.Answer = _ => new GenericWriteResult.Written("Leeks");

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Empty(matcher.Calls);
        Assert.Equal("Leeks", GenericOf(leeks)!.Name);
        Assert.Equal(1, run.Generated);
        Assert.Contains(names.Items, n => n.Key == "leeks" && n.Value.AisleId == "fruitVeg");
    }

    [Fact]
    public async Task Big_aisles_are_asked_in_parallel_batches_with_a_run_off_only_when_more_than_one_matches()
    {
        foreach (var n in new[] { "Apples", "Bananas", "Carrots", "Garlic", "Leeks", "Onions", "Peas" })
        {
            names.Add(n, "fruitVeg");
        }

        // Batches of 3: [Apples, Bananas, Carrots] [Garlic, Leeks, Onions] [Peas]. Two batches match: a run-off decides.
        var product = Known("leek", "fruitVeg");
        matcher.Answer = options => options.Count == 2
            ? new GenericMatchResult.Matched(options.Single(o => o.Name == "Leeks"), 0.99) // the run-off
            : options.FirstOrDefault(o => o.Name is "Carrots" or "Leeks") is { } o ? new GenericMatchResult.Matched(o, 0.95) : new GenericMatchResult.NoMatch();

        await UseCase(batchSize: 3).ExecuteAsync(CancellationToken.None);

        Assert.Equal([3, 3, 1], matcher.Calls.Take(3).Select(c => c.Count).Order().Reverse().ToArray()[..2].Concat([1]).ToArray());
        Assert.Equal(4, matcher.Calls.Count); // three batches and the run-off
        Assert.Equal(["Carrots", "Leeks"], matcher.Calls[3].Select(n => n.Name).Order());
        Assert.Equal("Leeks", GenericOf(product)!.Name);
    }

    [Fact]
    public async Task One_batch_matching_needs_no_run_off_and_none_matching_goes_to_the_word_retry()
    {
        foreach (var n in new[] { "Apples", "Bananas", "Carrots", "Garlic" })
        {
            names.Add(n, "fruitVeg");
        }

        var garlic = Known("Garlic", "fruitVeg");
        matcher.Answer = FakeMatcher.Picks("Garlic");
        await UseCase(batchSize: 3).ExecuteAsync(CancellationToken.None);
        Assert.Equal(2, matcher.Calls.Count); // two batches, no run-off
        Assert.Equal("Garlic", GenericOf(garlic)!.Name);
    }

    [Fact]
    public async Task The_word_retry_looks_in_other_aisles_for_a_shared_word()
    {
        names.Add("Frozen peas", "frozen");
        names.Add("Fish fingers", "frozen");
        names.Add("Apples", "fruitVeg");
        var peas = Known("Birds Eye Garden Peas 800g", "fruitVeg", brand: "Birds Eye");
        matcher.Answer = FakeMatcher.Picks("Frozen peas", 0.96);

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(["Apples"], matcher.Calls[0].Select(n => n.Name)); // the aisle
        Assert.Equal(["Frozen peas"], matcher.Calls[1].Select(n => n.Name)); // the shortlist: shares "pea"; not "Fish fingers"
        Assert.Equal(("Frozen peas", 1), (GenericOf(peas)!.Name, run.ByRetry));
    }

    [Fact]
    public async Task A_generated_name_that_normalises_to_an_existing_one_reuses_it_without_a_duplicate()
    {
        names.Add("Instant noodles", "pastaRiceGrains");
        var noodles = Known("Classic Soy Flavour Ramen Noodles", "pastaRiceGrains");
        matcher.Answer = FakeMatcher.Picks("Instant noodles", 0.71); // below the threshold
        writer.Answer = _ => new GenericWriteResult.Written("  instant   Noodles ");

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(("instant noodles", "Instant noodles"), (GenericOf(noodles)!.Id, GenericOf(noodles)!.Name));
        Assert.Single(names.Items);
        Assert.Equal(1, run.Reused);
    }

    [Fact]
    public async Task Foundry_gets_the_aisle_name_and_its_existing_names_as_examples()
    {
        names.Add("Mature cheddar", "dairyEggs");
        Known("Mini Mozzarella", "dairyEggs");
        writer.Answer = _ => new GenericWriteResult.Written("Mozzarella");

        await UseCase().ExecuteAsync(CancellationToken.None);

        var call = Assert.Single(writer.Calls);
        Assert.Equal(("Dairy & eggs", "Mini Mozzarella"), (call.Aisle, call.Product.Name));
        Assert.Equal(["Mature cheddar"], call.Examples);
    }

    [Fact]
    public async Task An_unusable_generated_name_falls_back_to_the_display_name_without_sizes()
    {
        var beans = Known("Baked Beans 415g", "tinsJars", brand: "Heinz");
        writer.Answer = _ => new GenericWriteResult.Written("Heinz baked beans");

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(("Baked Beans", GenericNameSource.Fallback), (GenericOf(beans)!.Name, GenericOf(beans)!.Source));
        Assert.Equal(1, run.Fallback);
    }

    [Fact]
    public async Task One_product_at_a_time_so_later_products_match_names_made_earlier_and_a_rerun_changes_nothing()
    {
        var first = Known("Udon Noodles", "pastaRiceGrains");
        var second = Known("UDON NOODLES", "pastaRiceGrains");
        writer.Answer = _ => new GenericWriteResult.Written("Udon noodles");
        matcher.Answer = FakeMatcher.Picks("Udon noodles");

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(GenericOf(first)!.Id, GenericOf(second)!.Id);
        Assert.Equal((1, 1), (run.Generated, run.InAisle));
        Assert.Single(writer.Calls);

        var again = await UseCase().ExecuteAsync(CancellationToken.None);
        Assert.Equal(0, again.Total);
    }

    [Fact]
    public async Task A_manual_name_is_never_overwritten()
    {
        var manual = new ProductGenericName("milk", "Milk", GenericNameSource.Manual, null, Now);
        var milk = Known("SEMI-SKIMMED BRITISH MILK", "dairyEggs", generic: manual);

        await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.Equal(manual, GenericOf(milk));
        Assert.Empty(matcher.Calls);
        Assert.Empty(writer.Calls);
    }

    [Theory]
    [InlineData("jev")]
    [InlineData("foundry")]
    public async Task When_jev_or_foundry_is_down_the_run_stops_and_products_wait_for_the_next(string down)
    {
        names.Add("Apples", "fruitVeg");
        var bananas = Known("Bananas", "fruitVeg");
        Known("Garlic", "fruitVeg");
        if (down == "jev")
        {
            matcher.Answer = _ => new GenericMatchResult.Unavailable("HTTP 503");
        }
        else
        {
            writer.Answer = _ => new GenericWriteResult.Unavailable("HTTP 429");
        }

        var run = await UseCase().ExecuteAsync(CancellationToken.None);

        Assert.True(run.Stopped);
        Assert.Null(GenericOf(bananas));
        Assert.Equal(1, down == "jev" ? matcher.Calls.Count : writer.Calls.Count); // stopped at the first product
    }

    [Fact]
    public async Task Choosing_an_existing_name_by_hand_saves_it_as_manual()
    {
        names.Add("Semi-skimmed milk", "dairyEggs");
        var milk = Known("SEMI-SKIMMED BRITISH MILK", "dairyEggs");
        var set = new SetProductGenericName(products, names, clock, new ListLogger<SetProductGenericName>());

        var result = Assert.IsType<SetGenericNameResult.Saved>(await set.ExecuteAsync(milk, "semi-skimmed milk", null, Gary, CancellationToken.None));

        Assert.False(result.Created);
        Assert.Equal(("semi-skimmed milk", GenericNameSource.Manual), (GenericOf(milk)!.Id, GenericOf(milk)!.Source));
    }

    [Fact]
    public async Task Typing_a_new_name_creates_it_in_the_products_aisle_unless_it_already_exists()
    {
        names.Add("Hot honey", "tinsJars");
        var honey = Known("Hot Honey", "tinsJars");
        var jam = Known("Strawberry Extra Jam", "tinsJars");
        var set = new SetProductGenericName(products, names, clock, new ListLogger<SetProductGenericName>());

        var existing = Assert.IsType<SetGenericNameResult.Saved>(await set.ExecuteAsync(honey, null, "hot  HONEY", Gary, CancellationToken.None));
        var created = Assert.IsType<SetGenericNameResult.Saved>(await set.ExecuteAsync(jam, null, "Strawberry jam", Gary, CancellationToken.None));

        Assert.False(existing.Created);
        Assert.True(created.Created);
        Assert.Equal("tinsJars", names.Items["strawberry jam"].AisleId);
        Assert.Equal(2, names.Items.Count);
    }

    [Fact]
    public async Task Bad_manual_choices_are_refused()
    {
        var jam = Known("Strawberry Extra Jam", "tinsJars", brand: "Maribel");
        var set = new SetProductGenericName(products, names, clock, new ListLogger<SetProductGenericName>());

        Assert.IsType<SetGenericNameResult.Invalid>(await set.ExecuteAsync(jam, null, null, Gary, CancellationToken.None));
        Assert.IsType<SetGenericNameResult.Invalid>(await set.ExecuteAsync(jam, "x", "y", Gary, CancellationToken.None));
        Assert.IsType<SetGenericNameResult.Invalid>(await set.ExecuteAsync(jam, "no such name", null, Gary, CancellationToken.None));
        Assert.IsType<SetGenericNameResult.Invalid>(await set.ExecuteAsync(jam, null, "Maribel jam", Gary, CancellationToken.None));
        Assert.IsType<SetGenericNameResult.NotFound>(await set.ExecuteAsync(Guid.NewGuid(), null, "Jam", Gary, CancellationToken.None));
    }

    [Fact]
    public async Task The_picker_lists_names_a_to_z_with_their_aisle_and_product_count()
    {
        names.Add("Mature cheddar", "dairyEggs");
        names.Add("Bran flakes", "breakfastCereal");
        var cheddar = new ProductGenericName("mature cheddar", "Mature cheddar", GenericNameSource.Foundry, null, Now);
        Known("Cheddar A", "dairyEggs", generic: cheddar);
        Known("Cheddar B", "dairyEggs", generic: cheddar);

        var list = await new ListGenericNames(names, products).ExecuteAsync(CancellationToken.None);

        Assert.Equal(
            [new GenericNameOption("bran flakes", "Bran flakes", "breakfastCereal", "Breakfast & cereal", 0), new GenericNameOption("mature cheddar", "Mature cheddar", "dairyEggs", "Dairy & eggs", 2)],
            list);
    }
}
