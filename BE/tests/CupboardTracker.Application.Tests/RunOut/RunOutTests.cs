using CupboardTracker.Application.Push;
using CupboardTracker.Application.RunOut;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.GenericNaming;
using CupboardTracker.Application.Tests.Push;
using CupboardTracker.Application.Tests.ShoppingList;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Tests.RunOut;

// Add to the shopping list when we run out (#89).
public sealed class RunOutTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");
    private static readonly CurrentUser Sam = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Sam");

    private readonly FixedClock clock = new(Now);
    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FakeGenericNames names = new();
    private readonly FakeShoppingList list = new();
    private readonly FakeListPublisher publisher = new();
    private readonly FakeBatches batches = new();
    private readonly ListLogger<AddWhenRunOut> logs = new();

    private RunOutGroups Groups() => new(products, names, units);

    private ShoppingListNotifier Notify() => new(publisher, new ListLogger<ShoppingListNotifier>());

    private ChangeShoppingListItem Changer() => new(list, Notify(), clock, new ListLogger<ChangeShoppingListItem>());

    private AddWhenRunOut RunOut() => new(
        Groups(), new AddToShoppingList(list, products, new FakeAisleScheduler(), Notify(), clock, new ListLogger<AddToShoppingList>()),
        Changer(), list, logs);

    private CheckOutFromApp CheckOut() => new(products, units, clock, new ListLogger<CheckOutFromApp>(), runOut: RunOut());

    private UndoCheckOut Undo() => new(units, new ListLogger<UndoCheckOut>(), products, runOut: RunOut());

    private Guid Product(string name, string? generic, int inStock)
    {
        var id = Guid.NewGuid();
        var product = Domain.Products.Product.Found(id, "5000354922831", new ProductDetails(name, null, null, null, []), Now, 1)
            .WithManualAisle("dairyEggs", Now);
        if (generic is not null)
        {
            var record = GenericName.Create(generic, "dairyEggs", Now, "manual");
            names.Items.TryAdd(record.Id, record);
            product = product.WithManualGenericName(record, Now);
        }

        products.Products[id] = product;
        for (var i = 0; i < inStock; i++)
        {
            units.Add(new Unit(Guid.NewGuid(), id, "5000354922831", UnitStatus.InStock, null, "scanner-in", Now.AddDays(-1), null, null, null, null));
        }

        return id;
    }

    private async Task TurnOn(string generic) => await new SetAddWhenOut(names, clock).ExecuteAsync(GenericNames.IdOf(generic), true, Gary, CancellationToken.None);

    private async Task<Guid> CheckOutOne(Guid productId) =>
        Assert.IsType<CheckOutFromAppResult.CheckedOut>(await CheckOut().ExecuteAsync(productId, Sam, CancellationToken.None)).UnitId;

    private IEnumerable<ShoppingListItem> OnList() => list.Items.Values.Select(v => v.Value).Where(i => !i.Removed);

    [Fact]
    public async Task One_brand_running_out_while_another_is_in_stock_adds_nothing()
    {
        var asda = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 1);
        Product("Cravendale Semi Skimmed", "Semi-skimmed milk", inStock: 1);
        await TurnOn("Semi-skimmed milk");

        await CheckOutOne(asda);

        Assert.Empty(OnList());
        Assert.Contains(logs.At(Microsoft.Extensions.Logging.LogLevel.Information), m => m.Contains("still has 1 in stock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gone_from_still_got_this_counts_as_running_out()
    {
        var asda = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 1);
        await TurnOn("Semi-skimmed milk");

        var gone = await new CheckOutUnit(products, units, clock, new ListLogger<CheckOutUnit>(), runOut: RunOut())
            .ExecuteAsync(units.InStock(asda).Single().UnitId, Sam, CancellationToken.None, CheckOutReasons.StockCheck);

        Assert.IsType<CheckOutUnitResult.CheckedOut>(gone);
        Assert.Equal("Semi-skimmed milk", Assert.Single(OnList()).Name);
    }

    [Fact]
    public async Task The_last_unit_across_every_brand_puts_the_generic_name_on_the_list_once()
    {
        var asda = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 1);
        var crav = Product("Cravendale Semi Skimmed", "Semi-skimmed milk", inStock: 1);
        await TurnOn("Semi-skimmed milk");

        await CheckOutOne(asda);
        await CheckOutOne(crav);

        var item = Assert.Single(OnList());
        Assert.Equal(("Semi-skimmed milk", ListItemSources.AutoRunOut, crav, "dairyEggs"), (item.Name, item.Source, item.ProductId, item.Aisle));
        Assert.Equal("Cupboard", item.AddedByName);
    }

    [Fact]
    public async Task Without_the_setting_nothing_is_ever_added()
    {
        var milk = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 1);

        await CheckOutOne(milk);

        Assert.Empty(OnList());
    }

    [Fact]
    public async Task Already_open_on_the_list_is_not_added_again()
    {
        var milk = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 1);
        await TurnOn("Semi-skimmed milk");
        await new AddToShoppingList(list, products, new FakeAisleScheduler(), Notify(), clock, new ListLogger<AddToShoppingList>())
            .ExecuteAsync(new AddListItem(Guid.NewGuid(), "c1", "semi-skimmed  milk", null, null, ListItemSources.Typed), Gary, CancellationToken.None);

        await CheckOutOne(milk);

        Assert.Equal(ListItemSources.Typed, Assert.Single(OnList()).Source);
    }

    [Fact]
    public async Task A_retried_check_out_event_never_adds_twice()
    {
        var milk = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 1);
        await TurnOn("Semi-skimmed milk");
        var unitId = await CheckOutOne(milk);
        await Changer().RemoveAsync(OnList().Single().Id, "someone-removed-it", CancellationToken.None);

        await RunOut().CheckedOutAsync(unitId, milk, Now, CancellationToken.None); // the same check-out, replayed

        Assert.Empty(OnList()); // same id: the add already happened, so it isn't recreated
    }

    [Fact]
    public async Task A_product_without_a_generic_name_counts_by_its_display_name_but_cant_be_turned_on()
    {
        var milk = Product("Fresh milk", generic: null, inStock: 1);
        Product("fresh  MILK", generic: null, inStock: 1);

        var status = await new GetRunOutStatus(Groups()).ExecuteAsync(milk, CancellationToken.None);

        Assert.Equal(new RunOutStatus("fresh milk", "Fresh milk", false, 2, false), status);
    }

    [Fact]
    public async Task Undoing_the_check_out_takes_the_automatic_item_off_again()
    {
        var milk = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 1);
        await TurnOn("Semi-skimmed milk");
        var unitId = await CheckOutOne(milk);
        Assert.Single(OnList());

        await Undo().ExecuteAsync(unitId, CancellationToken.None, Sam);

        Assert.Empty(OnList());
        Assert.Equal(("removed", 2L), (publisher.Sent[^1].Event, publisher.Sent[^1].Version));
    }

    [Fact]
    public async Task Undo_leaves_the_item_if_someone_ticked_it_since()
    {
        var milk = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 1);
        await TurnOn("Semi-skimmed milk");
        var unitId = await CheckOutOne(milk);
        await Changer().DoneAsync(OnList().Single().Id, "tick", Gary, CancellationToken.None);

        await Undo().ExecuteAsync(unitId, CancellationToken.None, Sam);

        Assert.Equal(ListItemStatus.Done, Assert.Single(OnList()).Status);
    }

    [Fact]
    public async Task Turning_it_on_from_one_brand_covers_every_product_with_that_generic_name()
    {
        var asda = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 2);
        var crav = Product("Cravendale Semi Skimmed", "Semi-skimmed milk", inStock: 1);

        await TurnOn("Semi-skimmed milk");

        var fromCrav = await new GetRunOutStatus(Groups()).ExecuteAsync(crav, CancellationToken.None);
        Assert.Equal(new RunOutStatus("semi-skimmed milk", "Semi-skimmed milk", true, 3, true), fromCrav);
        Assert.True((await new GetRunOutStatus(Groups()).ExecuteAsync(asda, CancellationToken.None))!.AddWhenOut);
        Assert.Equal((Gary.ObjectId, Now), (names.Items["semi-skimmed milk"].AddWhenOutBy, names.Items["semi-skimmed milk"].AddWhenOutAt));
    }

    [Fact]
    public async Task A_failure_is_a_warning_and_never_fails_the_check_out()
    {
        var milk = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 1);
        await TurnOn("Semi-skimmed milk");
        names.Fail = true;

        var result = await CheckOut().ExecuteAsync(milk, Sam, CancellationToken.None);

        Assert.IsType<CheckOutFromAppResult.CheckedOut>(result);
        Assert.Single(logs.At(Microsoft.Extensions.Logging.LogLevel.Warning));
    }

    // ---- The "Add to the shopping list?" sheet

    [Fact]
    public async Task The_sheet_for_a_batch_shows_its_removed_items_netted_with_whats_left_and_whether_theyre_on_the_list()
    {
        var milk = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 0);
        var hummus = Product("Tesco Hummus", "Hummus", inStock: 1);
        var bread = Product("Hovis", "Wholemeal bread", inStock: 1);
        var mine = Product("Gary's yoghurt", "Greek yoghurt", inStock: 0);
        await new AddToShoppingList(list, products, new FakeAisleScheduler(), Notify(), clock, new ListLogger<AddToShoppingList>())
            .ExecuteAsync(new AddListItem(Guid.NewGuid(), "c1", "Hummus", null, null, ListItemSources.Typed), Gary, CancellationToken.None);
        await TurnOn("Semi-skimmed milk");
        var batch = new StockChangeBatch(Guid.NewGuid(), Now, Now, [
            new StockChange("scan:1", StockChangeKind.Removed, "Asda Semi Skimmed Milk", Now, milk, Sam.ObjectId, "Sam"),
            new StockChange("scan:2", StockChangeKind.Removed, "Tesco Hummus", Now.AddMinutes(1), hummus, Sam.ObjectId, "Sam"),
            new StockChange("app:3:out", StockChangeKind.Removed, "Hovis", Now, bread, Sam.ObjectId, "Sam"),
            new StockChange("app:3:undo", StockChangeKind.Added, "Hovis", Now, bread, Sam.ObjectId, "Sam"), // undone: nets out
            new StockChange("app:4:out", StockChangeKind.Removed, "Gary's yoghurt", Now, mine, Gary.ObjectId, "Gary"), // Gary's own
        ]);
        await batches.SaveSentAsync(batch, CancellationToken.None);

        var choices = await new GetRunOutChoices(batches, Groups(), list).ForBatchAsync(batch.BatchId, Gary, CancellationToken.None);

        Assert.Equal(("Sam", Now.AddMinutes(1)), (choices!.CheckedOutBy, choices.At));
        Assert.Equal(
            [
                new RunOutChoice(milk, "semi-skimmed milk", "Semi-skimmed milk", 0, false, true, true),
                new RunOutChoice(hummus, "hummus", "Hummus", 1, true, false, true),
            ],
            choices.Items);
    }

    [Fact]
    public async Task A_batch_that_has_expired_or_never_existed_has_no_sheet()
    {
        Assert.Null(await new GetRunOutChoices(batches, Groups(), list).ForBatchAsync(Guid.NewGuid(), Gary, CancellationToken.None));
    }

    [Fact]
    public async Task Scanner_check_outs_say_the_scanner()
    {
        var milk = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 0);
        var batch = new StockChangeBatch(Guid.NewGuid(), Now, Now, [new StockChange("scan:1", StockChangeKind.Removed, "Milk", Now, milk)]);
        await batches.SaveSentAsync(batch, CancellationToken.None);

        var choices = await new GetRunOutChoices(batches, Groups(), list).ForBatchAsync(batch.BatchId, Gary, CancellationToken.None);

        Assert.Equal(GetRunOutChoices.Scanner, choices!.CheckedOutBy);
    }

    [Fact]
    public async Task The_sheet_for_one_product_has_its_row()
    {
        var milk = Product("Asda Semi Skimmed Milk", "Semi-skimmed milk", inStock: 1);

        var choices = await new GetRunOutChoices(batches, Groups(), list).ForProductAsync(milk, CancellationToken.None);

        Assert.Equal([new RunOutChoice(milk, "semi-skimmed milk", "Semi-skimmed milk", 1, false, false, true)], choices.Items);
    }
}
