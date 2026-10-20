using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.ShelfLives;
using CupboardTracker.Application.Tests.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.ShoppingList;

internal sealed class FakeShoppingList : IShoppingListRepository
{
    private int etag;

    public Dictionary<Guid, Versioned<ShoppingListItem>> Items { get; } = [];

    public Dictionary<string, ListMemory> Memory { get; } = [];

    public Task<Versioned<ShoppingListItem>?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.GetValueOrDefault(id));

    public Task<IReadOnlyList<Versioned<ShoppingListItem>>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Versioned<ShoppingListItem>>>([.. Items.Values]);

    public Task<bool> TryCreateAsync(ShoppingListItem item, CancellationToken cancellationToken)
    {
        if (Items.ContainsKey(item.Id))
        {
            return Task.FromResult(false);
        }

        Items[item.Id] = new(item, $"e{++etag}");
        return Task.FromResult(true);
    }

    public Task<bool> TryReplaceAsync(Versioned<ShoppingListItem> current, ShoppingListItem updated, CancellationToken cancellationToken)
    {
        if (Items.GetValueOrDefault(updated.Id)?.ETag != current.ETag)
        {
            return Task.FromResult(false);
        }

        Items[updated.Id] = new(updated, $"e{++etag}");
        return Task.FromResult(true);
    }

    public Task<ListMemory?> GetMemoryAsync(string normalizedName, CancellationToken cancellationToken) =>
        Task.FromResult(Memory.GetValueOrDefault(normalizedName));

    public Task<IReadOnlyList<ListMemory>> ListMemoryAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ListMemory>>([.. Memory.Values]);

    public Task SaveMemoryAsync(ListMemory memory, CancellationToken cancellationToken)
    {
        Memory[memory.NormalizedName] = memory;
        return Task.CompletedTask;
    }
}

internal sealed class FakeListPublisher : IShoppingListPublisher
{
    public List<(string Event, Guid Id, long Version)> Sent { get; } = [];

    public bool Fail { get; set; }

    public Task ItemChangedAsync(ShoppingListItem item, CancellationToken cancellationToken) => Send("changed", item.Id, item.Version);

    public Task ItemRemovedAsync(Guid id, long version, CancellationToken cancellationToken) => Send("removed", id, version);

    public Task ClearedAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            Send("cleared", id, 0);
        }

        return Task.CompletedTask;
    }

    private Task Send(string name, Guid id, long version)
    {
        if (Fail)
        {
            throw new InvalidOperationException("SignalR is down");
        }

        Sent.Add((name, id, version));
        return Task.CompletedTask;
    }
}

internal sealed class FakeAisleScheduler : IListAisleScheduler
{
    public List<Guid> Scheduled { get; } = [];

    public Task ScheduleAsync(Guid itemId, CancellationToken cancellationToken)
    {
        Scheduled.Add(itemId);
        return Task.CompletedTask;
    }
}

public sealed class ShoppingListTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");
    private static readonly CurrentUser Sam = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Sam");

    private readonly FixedClock clock = new(Now);
    private readonly FakeShoppingList list = new();
    private readonly FakeListPublisher publisher = new();
    private readonly FakeAisleScheduler scheduler = new();
    private readonly FakeProducts products = new();
    private readonly FakeStockQueries stockQueries = new();
    private readonly FakeEstimator jev = new();
    private readonly ListLogger<ShoppingListNotifier> notifyLogs = new();

    private ShoppingListNotifier Notify() => new(publisher, notifyLogs);

    private AddToShoppingList Adder() => new(list, products, scheduler, Notify(), clock, new ListLogger<AddToShoppingList>());

    private ChangeShoppingListItem Changer() => new(list, Notify(), clock, new ListLogger<ChangeShoppingListItem>());

    private ClearDoneShoppingListItems Clearer() => new(list, Notify(), clock, new ListLogger<ClearDoneShoppingListItems>());

    private AssignListAisle Aisler() => new(list, jev, Notify(), clock, new ListLogger<AssignListAisle>());

    private SuggestListItems Suggester() => new(list, products, new GetStock(stockQueries, products, TestSettings.Stock(), clock));

    private async Task<ShoppingListItem> Add(string? name, string? note = null, Guid? productId = null, Guid? id = null, string? changeId = null,
        CurrentUser? user = null, string source = ListItemSources.Typed)
    {
        var result = await Adder().ExecuteAsync(
            new AddListItem(id ?? Guid.NewGuid(), changeId ?? Guid.NewGuid().ToString(), name, note, productId, source), user ?? Gary, CancellationToken.None);
        return Assert.IsType<ListChangeResult.Changed>(result).Item;
    }

    private Guid Product(string name, string? aisle = null, string? generic = null, int inStock = 0)
    {
        var id = Guid.NewGuid();
        var product = Domain.Products.Product.Found(id, "5000354922831", new ProductDetails(name, null, null, null, []), Now, 1);
        if (aisle is not null)
        {
            product = product.WithManualAisle(aisle, Now);
        }

        if (generic is not null)
        {
            product = product.WithManualGenericName(GenericName.Create(generic, aisle ?? "other", Now, "manual"), Now);
        }

        products.Products[id] = product;
        for (var i = 0; i < inStock; i++)
        {
            stockQueries.Units.Add(new StockUnit(id, null, Now.AddDays(-1)));
        }

        return id;
    }

    // ---- Adding

    [Fact]
    public async Task A_typed_item_is_added_at_once_with_no_aisle_and_its_aisle_is_queued()
    {
        var item = await Add("  Kitchen   Foil ", note: "the big roll");

        Assert.Equal(("Kitchen   Foil", "kitchen foil", "the big roll", ListItemStatus.Open, 1L), (item.Name, item.NormalizedName, item.Note, item.Status, item.Version));
        Assert.True(item.FindingAisle);
        Assert.Equal(("Gary", Gary.ObjectId), (item.AddedByName, item.AddedBy));
        Assert.Equal([item.Id], scheduler.Scheduled);
        Assert.Equal([("changed", item.Id, 1L)], publisher.Sent);
    }

    [Fact]
    public async Task Adding_is_idempotent_by_item_id()
    {
        var id = Guid.NewGuid();
        var first = await Add("Bananas", id: id, changeId: "c1");
        var again = await Add("Bananas", id: id, changeId: "c1");

        Assert.Equal(first, again);
        Assert.Single(list.Items);
        Assert.Single(publisher.Sent);
    }

    [Fact]
    public async Task A_name_already_open_isnt_added_twice_but_a_new_note_is_merged_in()
    {
        var milk = await Add("Semi-skimmed milk");
        var same = await Add("semi-skimmed  MILK");
        var withNote = await Add("Semi-skimmed milk", note: "2");
        var withAnother = await Add("Semi-skimmed milk", note: "the big one");

        Assert.Single(list.Items);
        Assert.Equal(milk.Id, same.Id);
        Assert.Equal(("2", 2L), (withNote.Note, withNote.Version));
        Assert.Equal(("2, the big one", 3L), (withAnother.Note, withAnother.Version));
    }

    [Fact]
    public async Task An_item_from_a_product_takes_its_aisle_and_generic_name()
    {
        var milk = Product("SEMI-SKIMMED BRITISH MILK", aisle: "dairyEggs", generic: "Semi-skimmed milk");

        var item = await Add(null, productId: milk, source: ListItemSources.ItemDetail);

        Assert.Equal(("Semi-skimmed milk", "dairyEggs", ListAisleSources.Product, milk), (item.Name, item.Aisle, item.AisleSource, item.ProductId));
        Assert.Empty(scheduler.Scheduled);
    }

    [Fact]
    public async Task A_previous_aisle_for_the_same_name_is_reused_without_asking_jev()
    {
        list.Memory["kitchen foil"] = new ListMemory("kitchen foil", "Kitchen foil", "other", 3, Now.AddDays(-5));

        var item = await Add("Kitchen foil");

        Assert.Equal(("other", ListAisleSources.Jev), (item.Aisle, item.AisleSource));
        Assert.Empty(scheduler.Scheduled);
        Assert.Equal(4, list.Memory["kitchen foil"].TimesAdded);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Bread", "a note that is far too long to fit on the shopping list row at all")]
    public async Task Bad_adds_are_invalid(string name, string? note)
    {
        var result = await Adder().ExecuteAsync(new AddListItem(Guid.NewGuid(), "c", name, note, null, ListItemSources.Typed), Gary, CancellationToken.None);
        Assert.IsType<ListChangeResult.Invalid>(result);
        Assert.IsType<ListChangeResult.Invalid>(await Adder().ExecuteAsync(new AddListItem(Guid.Empty, "c", "Bread", null, null, "typed"), Gary, CancellationToken.None));
        Assert.IsType<ListChangeResult.Invalid>(await Adder().ExecuteAsync(new AddListItem(Guid.NewGuid(), "", "Bread", null, null, "typed"), Gary, CancellationToken.None));
    }

    // ---- Ticking, putting back, removing

    [Fact]
    public async Task Tick_untick_and_remove_each_bump_the_version_and_are_idempotent_by_change_id()
    {
        var item = await Add("Bread");

        var done = Assert.IsType<ListChangeResult.Changed>(await Changer().DoneAsync(item.Id, "t1", Sam, CancellationToken.None)).Item;
        var repeat = Assert.IsType<ListChangeResult.Changed>(await Changer().DoneAsync(item.Id, "t1", Sam, CancellationToken.None)).Item;
        var back = Assert.IsType<ListChangeResult.Changed>(await Changer().UndoneAsync(item.Id, "t2", CancellationToken.None)).Item;
        var gone = Assert.IsType<ListChangeResult.Changed>(await Changer().RemoveAsync(item.Id, "t3", CancellationToken.None)).Item;

        Assert.Equal((ListItemStatus.Done, "Sam", 2L), (done.Status, done.DoneByName, done.Version));
        Assert.Equal(done, repeat);
        Assert.Equal((ListItemStatus.Open, null, 3L), (back.Status, back.DoneByName, back.Version));
        Assert.Equal((true, 4L), (gone.Removed, gone.Version));
        Assert.Equal(["changed", "changed", "changed", "removed"], publisher.Sent.Select(s => s.Event));
        Assert.Equal(4L, publisher.Sent[^1].Version);
        Assert.IsType<ListChangeResult.NotFound>(await Changer().DoneAsync(Guid.NewGuid(), "x", Gary, CancellationToken.None));
    }

    [Fact]
    public async Task Clear_ticked_tombstones_every_done_item_and_the_timer_only_ones_over_a_day_old()
    {
        var old = await Add("Eggs");
        var recent = await Add("Bread");
        var open = await Add("Milk");
        await Changer().DoneAsync(old.Id, "d1", Gary, CancellationToken.None);
        clock.Now = Now.AddHours(20);
        await Changer().DoneAsync(recent.Id, "d2", Gary, CancellationToken.None);
        clock.Now = Now.AddHours(25);

        Assert.Equal([old.Id], await Clearer().ExecuteAsync(onlyOlderThanADay: true, CancellationToken.None));
        Assert.Equal([recent.Id], await Clearer().ExecuteAsync(onlyOlderThanADay: false, CancellationToken.None));
        Assert.False(list.Items[open.Id].Value.Removed);
        Assert.True(list.Items[old.Id].Value.Removed && list.Items[recent.Id].Value.Removed);
    }

    // ---- Reading and catching up

    [Fact]
    public async Task The_list_is_open_items_oldest_first_and_recently_ticked_and_since_returns_changes_and_tombstones()
    {
        var bread = await Add("Bread");
        clock.Now = Now.AddMinutes(1);
        var milk = await Add("Milk");
        clock.Now = Now.AddMinutes(2);
        var eggs = await Add("Eggs");
        await Changer().DoneAsync(eggs.Id, "d", Gary, CancellationToken.None);
        clock.Now = Now.AddMinutes(3);
        var asOf = (await new GetShoppingList(list, clock).ExecuteAsync(null, CancellationToken.None)).AsOf;
        clock.Now = Now.AddMinutes(10);
        await Changer().RemoveAsync(bread.Id, "r", CancellationToken.None);

        var full = await new GetShoppingList(list, clock).ExecuteAsync(null, CancellationToken.None);
        var since = await new GetShoppingList(list, clock).ExecuteAsync(asOf, CancellationToken.None);

        Assert.Equal([milk.Id, eggs.Id], full.Items.Select(i => i.Id));
        Assert.Equal([bread.Id], since.Items.Select(i => i.Id));
        Assert.True(since.Items[0].Removed);
        Assert.Equal(Now.AddMinutes(10), full.AsOf);
    }

    // ---- Suggestions

    [Fact]
    public async Task Suggestions_use_generic_names_sum_stock_across_brands_and_leave_out_open_names()
    {
        Product("Asda Semi Skimmed Milk", aisle: "dairyEggs", generic: "Semi-skimmed milk", inStock: 2);
        Product("Coop Semi-Skimmed Milk", aisle: "dairyEggs", generic: "Semi-skimmed milk", inStock: 1);
        Product("Oat milk", aisle: "dairyEggs", generic: "Oat milk");
        Product("Milky Way");
        list.Memory["milk chocolate"] = new ListMemory("milk chocolate", "Milk chocolate", "snacksSweets", 5, Now.AddDays(-1));
        await Add("Oat milk");

        var suggestions = await Suggester().ExecuteAsync("milk", CancellationToken.None);

        Assert.Equal(["Milk chocolate", "Milky Way", "Semi-skimmed milk"], suggestions.Select(s => s.Name).Take(3).Order());
        var semi = suggestions.Single(s => s.Name == "Semi-skimmed milk");
        Assert.Equal(3, semi.InStock);
        Assert.NotNull(semi.ProductId);
        Assert.DoesNotContain(suggestions, s => s.Name == "Oat milk"); // already open
        Assert.Equal("Milk chocolate", suggestions[0].Name); // starts with it, and used most
        Assert.Empty(await Suggester().ExecuteAsync("  ", CancellationToken.None));
    }

    [Fact]
    public async Task Suggestions_rank_starts_with_then_generic_names_then_use()
    {
        Product("Wholemeal bread", generic: "Wholemeal bread");
        Product("Breadsticks");
        Product("Garlic bread", generic: "Garlic bread");
        list.Memory["bread"] = new ListMemory("bread", "Bread", null, 9, Now);

        var names = (await Suggester().ExecuteAsync("bread", CancellationToken.None)).Select(s => s.Name).ToList();

        Assert.Equal(["Bread", "Breadsticks", "Garlic bread", "Wholemeal bread"], names);
    }

    // ---- Aisles

    [Fact]
    public async Task A_typed_items_aisle_comes_from_jev_is_remembered_and_published()
    {
        var item = await Add("Kitchen foil");
        jev.ClassifyAnswer = _ => new ClassificationResult.Classified(new Classification("other", 0.9, null, 0));

        Assert.True(await Aisler().ExecuteAsync(item.Id, CancellationToken.None));

        var stored = list.Items[item.Id].Value;
        Assert.Equal(("other", ListAisleSources.Jev, 2L), (stored.Aisle, stored.AisleSource, stored.Version));
        Assert.Equal(("Kitchen foil", true, false), (jev.ClassifyCalls[0].Product.Name, jev.ClassifyCalls[0].Aisle, jev.ClassifyCalls[0].Icon));
        Assert.Equal("other", list.Memory["kitchen foil"].Aisle);
        Assert.Equal(("changed", item.Id, 2L), publisher.Sent[^1]);
        Assert.False(await Aisler().ExecuteAsync(item.Id, CancellationToken.None)); // done already
    }

    [Fact]
    public async Task A_tick_while_jev_thinks_keeps_the_tick_and_still_saves_the_aisle()
    {
        var item = await Add("Kitchen foil");
        jev.ClassifyAnswer = _ =>
        {
            // Someone ticks it off (or an offline phone's replay lands) while Jev is answering (#85).
            Changer().DoneAsync(item.Id, "tick-meanwhile", Gary, CancellationToken.None).GetAwaiter().GetResult();
            return new ClassificationResult.Classified(new Classification("other", 0.9, null, 0));
        };

        Assert.True(await Aisler().ExecuteAsync(item.Id, CancellationToken.None));

        var stored = list.Items[item.Id].Value;
        Assert.Equal(("other", ListAisleSources.Jev, ListItemStatus.Done, 3L), (stored.Aisle, stored.AisleSource, stored.Status, stored.Version));
        Assert.Single(jev.ClassifyCalls); // Jev isn't asked again
    }

    [Fact]
    public async Task Jev_down_puts_it_in_other_for_now_and_the_timer_retries()
    {
        var item = await Add("Kitchen foil");
        jev.ClassifyAnswer = _ => new ClassificationResult.Unavailable("timeout");

        await Aisler().ExecuteAsync(item.Id, CancellationToken.None);
        Assert.Equal(("other", ListAisleSources.Fallback), (list.Items[item.Id].Value.Aisle, list.Items[item.Id].Value.AisleSource));

        jev.ClassifyAnswer = _ => new ClassificationResult.Classified(new Classification("bakingSpices", 0.7, null, 0));
        Assert.Equal(1, await Aisler().RetryAsync(CancellationToken.None));
        Assert.Equal(("bakingSpices", ListAisleSources.Jev), (list.Items[item.Id].Value.Aisle, list.Items[item.Id].Value.AisleSource));
    }

    // ---- Live updates are best-effort

    [Fact]
    public async Task A_publish_failure_is_a_warning_and_never_fails_the_change()
    {
        publisher.Fail = true;

        var item = await Add("Bread");
        await Changer().DoneAsync(item.Id, "d", Gary, CancellationToken.None);

        Assert.Equal(ListItemStatus.Done, list.Items[item.Id].Value.Status);
        Assert.Equal(2, notifyLogs.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    // ---- Offline changes arriving late (#85)

    [Fact]
    public async Task A_late_tick_from_an_offline_phone_cant_undo_a_newer_untick()
    {
        var eggs = await Add("Eggs");
        clock.Now = Now.AddMinutes(20);
        await Changer().DoneAsync(eggs.Id, "online-tick", Sam, CancellationToken.None, Now.AddMinutes(1)); // Sam ticks at 10:01
        await Changer().UndoneAsync(eggs.Id, "online-untick", CancellationToken.None, Now.AddMinutes(5)); // and unticks at 10:05

        // Gary ticked at 10:00 while offline; it arrives at 10:20.
        var late = Assert.IsType<ListChangeResult.Changed>(await Changer().DoneAsync(eggs.Id, "offline-tick", Gary, CancellationToken.None, Now));

        Assert.Equal(ListItemStatus.Open, late.Item.Status);
        Assert.Equal(Now.AddMinutes(5), list.Items[eggs.Id].Value.StatusChangedAt);
    }

    [Fact]
    public async Task A_newer_offline_tick_applies_with_its_own_time_and_a_future_time_counts_as_now()
    {
        var milk = await Add("Milk");
        clock.Now = Now.AddMinutes(30);

        var ticked = Assert.IsType<ListChangeResult.Changed>(await Changer().DoneAsync(milk.Id, "t", Gary, CancellationToken.None, Now.AddMinutes(10))).Item;
        Assert.Equal((ListItemStatus.Done, Now.AddMinutes(10)), (ticked.Status, ticked.DoneAt));

        var back = Assert.IsType<ListChangeResult.Changed>(await Changer().UndoneAsync(milk.Id, "u", CancellationToken.None, Now.AddDays(1))).Item;
        Assert.Equal((ListItemStatus.Open, Now.AddMinutes(30)), (back.Status, back.StatusChangedAt));
    }

    [Fact]
    public async Task Replaying_an_older_tick_after_a_newer_change_does_nothing()
    {
        var bread = await Add("Bread");
        clock.Now = Now.AddMinutes(10);
        await Changer().DoneAsync(bread.Id, "t1", Gary, CancellationToken.None, Now.AddMinutes(1));
        await Changer().UndoneAsync(bread.Id, "u1", CancellationToken.None, Now.AddMinutes(2));

        await Changer().DoneAsync(bread.Id, "t1", Gary, CancellationToken.None, Now.AddMinutes(1)); // the outbox resends t1

        Assert.Equal(ListItemStatus.Open, list.Items[bread.Id].Value.Status);
    }
}
