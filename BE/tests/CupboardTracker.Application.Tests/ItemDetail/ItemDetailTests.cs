using CupboardTracker.Application.Products;
using CupboardTracker.Application.Scans;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Tests.ShelfLives;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Tests.ItemDetail;

/// <summary>Item detail (#53): units soonest first, per-unit check-out, manual dates and shelf life, editing details.</summary>
public sealed class ItemDetailTests
{
    // 08:00 UTC on 18 Oct 2026 is 09:00 BST: today is the 18th in the UK.
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 18);
    private static readonly CurrentUser Gary = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "Gary");

    private readonly FakeProducts products = new();
    private readonly FakeUnits units = new();
    private readonly FixedClock clock = new(Now);

    private sealed class NoDevices : IDeviceRepository
    {
        public Task<Device?> GetAsync(string deviceId, CancellationToken cancellationToken) => Task.FromResult<Device?>(null);

        public Task UpsertAsync(Device device, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private Guid Milk(int? shelfLifeDays = 7, string source = "jev")
    {
        var id = Guid.NewGuid();
        var product = Product.Found(id, "4056489899112", new ProductDetails("SEMI-SKIMMED BRITISH MILK", "Dairy Manor", "2.272 L", null, ["en:milks"]), Now, 1);
        products.Products[id] = shelfLifeDays is { } days
            ? product.WithShelfLife(new ShelfLife(days, Storage.Fridge, 0.83, false, source, Now))
            : product;
        return id;
    }

    private Unit Add(Guid productId, int boughtDaysAgo, int? expiresInDays, UnitStatus status = UnitStatus.InStock)
    {
        var id = Guid.NewGuid();
        var unit = new Unit(id, productId, "4056489899112", status, id, "scanner-in", Now.AddDays(-boughtDaysAgo), null, null, null,
            expiresInDays is { } d ? Today.AddDays(d) : null, null, expiresInDays is null ? null : ExpirySource.ShelfLife);
        units.Add(unit);
        return unit;
    }

    private GetProduct Get() => new(products, units, new NoDevices(), TestSettings.Stock(), clock);

    [Fact]
    public async Task Units_are_listed_soonest_first_with_days_until_and_the_shelf_life()
    {
        var milk = Milk();
        var later = Add(milk, 1, 6);
        var noDate = Add(milk, 0, null);
        var soonest = Add(milk, 5, 2);
        Add(milk, 9, -2, UnitStatus.CheckedOut);

        var view = (await Get().ExecuteAsync(milk, CancellationToken.None))!;

        Assert.Equal([soonest.UnitId, later.UnitId, noDate.UnitId], view.InStockUnits.Select(u => u.UnitId));
        Assert.Equal([2, 6, (int?)null], view.InStockUnits.Select(u => u.DaysUntilExpiry));
        Assert.Equal((7, "jev"), (view.ShelfLifeDays!.Value, view.ShelfLifeSource));
    }

    [Fact]
    public async Task Checks_out_that_unit_only()
    {
        var milk = Milk();
        var first = Add(milk, 5, 2);
        var second = Add(milk, 1, 6);

        var result = await new CheckOutUnit(products, units, clock, new ListLogger<CheckOutUnit>())
            .ExecuteAsync(second.UnitId, Gary, CancellationToken.None);

        Assert.Equal(new CheckOutUnitResult.CheckedOut(second.UnitId, milk, "SEMI-SKIMMED BRITISH MILK", 1), result);
        var stored = units.Units[second.UnitId].Unit;
        Assert.Equal((UnitStatus.CheckedOut, "ui", Gary.ObjectId, Now), (stored.Status, stored.CheckOutSource, stored.CheckedOutBy, stored.CheckedOutAt));
        Assert.Equal(UnitStatus.InStock, units.Units[first.UnitId].Unit.Status);
    }

    [Fact]
    public async Task Checking_out_a_unit_that_is_gone_is_a_conflict_and_an_unknown_one_not_found()
    {
        var milk = Milk();
        var gone = Add(milk, 5, 2, UnitStatus.CheckedOut);
        var stolen = Add(milk, 1, 6);
        units.StolenBeforeReplace.Add(stolen.UnitId); // a scanner takes it first
        var useCase = new CheckOutUnit(products, units, clock, new ListLogger<CheckOutUnit>());

        Assert.IsType<CheckOutUnitResult.NotInStock>(await useCase.ExecuteAsync(gone.UnitId, Gary, CancellationToken.None));
        Assert.IsType<CheckOutUnitResult.NotInStock>(await useCase.ExecuteAsync(stolen.UnitId, Gary, CancellationToken.None));
        Assert.IsType<CheckOutUnitResult.NotFound>(await useCase.ExecuteAsync(Guid.NewGuid(), Gary, CancellationToken.None));
    }

    [Fact]
    public async Task Undo_puts_a_unit_checked_out_from_item_detail_back()
    {
        var milk = Milk();
        var unit = Add(milk, 5, 2);
        await new CheckOutUnit(products, units, clock, new ListLogger<CheckOutUnit>()).ExecuteAsync(unit.UnitId, Gary, CancellationToken.None);

        await new UndoCheckOut(units, new ListLogger<UndoCheckOut>()).ExecuteAsync(unit.UnitId, CancellationToken.None);

        Assert.Equal(unit, units.Units[unit.UnitId].Unit);
    }

    [Fact]
    public async Task A_manual_date_changes_that_unit_only_and_survives_a_shelf_life_change_and_fill()
    {
        var milk = Milk();
        var unit = Add(milk, 5, 2);
        var other = Add(milk, 1, 6);
        var unfilled = Add(milk, 0, null);
        var setDate = new SetUnitExpiry(units, new ListLogger<SetUnitExpiry>());

        var result = await setDate.ExecuteAsync(unit.UnitId, "2026-10-25", Gary, CancellationToken.None);

        Assert.Equal(new SetUnitExpiryResult.Saved(unit.UnitId, milk, new DateOnly(2026, 10, 25)), result);
        Assert.Equal((new DateOnly(2026, 10, 25), ExpirySource.Manual),
            (units.Units[unit.UnitId].Unit.EstimatedExpiryDate!.Value, units.Units[unit.UnitId].Unit.ExpirySource!.Value));
        Assert.Equal(Today.AddDays(6), units.Units[other.UnitId].Unit.EstimatedExpiryDate);

        // A new shelf life, then the timer's fill: the manual date and the other dated unit are untouched.
        await new SetShelfLife(products, clock, new ListLogger<SetShelfLife>()).ExecuteAsync(milk, 30, Gary, CancellationToken.None);
        await new EstimateShelfLives(products, units, new FakeEstimator(), TestSettings.Stock(), clock,
            new ListLogger<EstimateShelfLives>()).ExecuteAsync(CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 10, 25), units.Units[unit.UnitId].Unit.EstimatedExpiryDate);
        Assert.Equal(Today.AddDays(6), units.Units[other.UnitId].Unit.EstimatedExpiryDate);
        Assert.Equal(Today.AddDays(30), units.Units[unfilled.UnitId].Unit.EstimatedExpiryDate); // it had none yet
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("25/10/2026")]
    [InlineData("2026-02-30")]
    [InlineData("1999-12-31")]
    [InlineData("2101-01-01")]
    public async Task Invalid_dates_are_refused(string? date)
    {
        var unit = Add(Milk(), 5, 2);
        Assert.IsType<SetUnitExpiryResult.Invalid>(
            await new SetUnitExpiry(units, new ListLogger<SetUnitExpiry>()).ExecuteAsync(unit.UnitId, date, Gary, CancellationToken.None));
        Assert.Equal(Today.AddDays(2), units.Units[unit.UnitId].Unit.EstimatedExpiryDate);
    }

    [Fact]
    public async Task A_date_for_a_unit_not_in_stock_is_refused()
    {
        var gone = Add(Milk(), 5, 2, UnitStatus.CheckedOut);
        var setDate = new SetUnitExpiry(units, new ListLogger<SetUnitExpiry>());
        Assert.IsType<SetUnitExpiryResult.Conflict>(await setDate.ExecuteAsync(gone.UnitId, "2026-10-25", Gary, CancellationToken.None));
        Assert.IsType<SetUnitExpiryResult.NotFound>(await setDate.ExecuteAsync(Guid.NewGuid(), "2026-10-25", Gary, CancellationToken.None));
    }

    [Fact]
    public async Task A_manual_shelf_life_applies_to_new_check_ins_only_and_jev_never_overwrites_it()
    {
        var milk = Milk(shelfLifeDays: null);
        var existing = Add(milk, 5, 2);

        var result = await new SetShelfLife(products, clock, new ListLogger<SetShelfLife>()).ExecuteAsync(milk, 14, Gary, CancellationToken.None);

        Assert.Equal(new SetShelfLifeResult.Saved(milk, 14), result);
        var shelfLife = products.Products[milk].ShelfLife!;
        Assert.Equal((14, "manual", true), (shelfLife.Days, shelfLife.Source, shelfLife.IsManual));
        Assert.Equal(Today.AddDays(2), units.Units[existing.UnitId].Unit.EstimatedExpiryDate);

        var estimator = new FakeEstimator();
        await new EstimateShelfLives(products, units, estimator, TestSettings.Stock(), clock, new ListLogger<EstimateShelfLives>())
            .ExecuteAsync(CancellationToken.None);
        Assert.Empty(estimator.Calls);
        Assert.Equal(14, products.Products[milk].ShelfLife!.Days);

        // A new check-in uses it.
        products.Lookups["4056489899112"] = milk;
        var resolver = new ProductResolver(products, new FakeLookup(), clock, new ListLogger<ProductResolver>());
        var scan = TestScans.Make("4056489899112", Role.In, at: Now);
        await new CheckInScan(resolver, units, TestSettings.Stock(), new FakeFeed().Notifier(), clock, new ListLogger<CheckInScan>()).ExecuteAsync(scan, CancellationToken.None);
        Assert.Equal(Today.AddDays(14), units.Units[scan.ScanId].Unit.EstimatedExpiryDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task Shelf_life_days_must_be_sensible(int? days) =>
        Assert.IsType<SetShelfLifeResult.Invalid>(
            await new SetShelfLife(products, clock, new ListLogger<SetShelfLife>()).ExecuteAsync(Milk(), days, Gary, CancellationToken.None));

    [Fact]
    public async Task Shelf_life_needs_a_known_product()
    {
        var id = Guid.NewGuid();
        products.Products[id] = Product.NotFound(id, "20055363", Now, 1);
        var useCase = new SetShelfLife(products, clock, new ListLogger<SetShelfLife>());

        Assert.IsType<SetShelfLifeResult.Conflict>(await useCase.ExecuteAsync(id, 7, Gary, CancellationToken.None));
        Assert.IsType<SetShelfLifeResult.NotFound>(await useCase.ExecuteAsync(Guid.NewGuid(), 7, Gary, CancellationToken.None));
    }

    [Fact]
    public async Task Editing_details_makes_the_product_manual_and_keeps_its_shelf_life()
    {
        var milk = Milk();
        var useCase = new EditProductDetails(products, clock, new ListLogger<EditProductDetails>());

        var result = await useCase.ExecuteAsync(milk, new NameProductCommand("  Semi-skimmed milk ", "Dairy Manor", " "), Gary, CancellationToken.None);

        Assert.Equal(new EditProductDetailsResult.Saved(milk, "Semi-skimmed milk", "Dairy Manor", null), result);
        var product = products.Products[milk];
        Assert.Equal((ProductSource.Manual, "Semi-skimmed milk", "Dairy Manor", (string?)null, Now, Gary.ObjectId),
            (product.Source!.Value, product.DisplayName, product.Details!.Brand, product.Details.Size, product.EditedAt!.Value, product.EditedBy!.Value));
        Assert.Equal(7, product.ShelfLife!.Days);
        Assert.Equal(["en:milks"], product.Details.CategoryTags);
    }

    [Fact]
    public async Task An_edited_product_is_never_looked_up_again()
    {
        var milk = Milk();
        products.Lookups["4056489899112"] = milk;
        await new EditProductDetails(products, clock, new ListLogger<EditProductDetails>())
            .ExecuteAsync(milk, new NameProductCommand("Semi-skimmed milk", null, null), Gary, CancellationToken.None);
        var lookup = new FakeLookup();

        var resolved = await new ProductResolver(products, lookup, clock, new ListLogger<ProductResolver>())
            .ResolveAsync("4056489899112", CancellationToken.None);

        Assert.Equal("Semi-skimmed milk", resolved.DisplayName);
        Assert.Empty(lookup.Calls);
    }

    [Fact]
    public async Task Editing_validates_like_naming_and_needs_a_known_product()
    {
        var milk = Milk();
        var useCase = new EditProductDetails(products, clock, new ListLogger<EditProductDetails>());
        var unknown = Guid.NewGuid();
        products.Products[unknown] = Product.NotFound(unknown, "20055363", Now, 1);

        var invalid = Assert.IsType<EditProductDetailsResult.Invalid>(
            await useCase.ExecuteAsync(milk, new NameProductCommand(" ", new string('b', 61), null), Gary, CancellationToken.None));
        Assert.Equal(["brand", "name"], invalid.Errors.Keys.Order());
        Assert.IsType<EditProductDetailsResult.Conflict>(
            await useCase.ExecuteAsync(unknown, new NameProductCommand("Naan", null, null), Gary, CancellationToken.None));
        Assert.IsType<EditProductDetailsResult.NotFound>(
            await useCase.ExecuteAsync(Guid.NewGuid(), new NameProductCommand("Naan", null, null), Gary, CancellationToken.None));
        Assert.Equal("SEMI-SKIMMED BRITISH MILK", products.Products[milk].DisplayName);
    }

    [Fact]
    public async Task A_concurrent_change_is_a_conflict_not_an_overwrite()
    {
        var milk = Milk();
        products.BeforeWrite = () => products.Versions[milk] = products.Versions.GetValueOrDefault(milk) + 1;

        Assert.IsType<EditProductDetailsResult.Conflict>(await new EditProductDetails(products, clock, new ListLogger<EditProductDetails>())
            .ExecuteAsync(milk, new NameProductCommand("Milk", null, null), Gary, CancellationToken.None));
        Assert.IsType<SetShelfLifeResult.Conflict>(await new SetShelfLife(products, clock, new ListLogger<SetShelfLife>())
            .ExecuteAsync(milk, 7, Gary, CancellationToken.None));
        Assert.Equal("SEMI-SKIMMED BRITISH MILK", products.Products[milk].DisplayName);
    }
}
