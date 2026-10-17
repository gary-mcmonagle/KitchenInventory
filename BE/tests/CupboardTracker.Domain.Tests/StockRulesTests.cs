using CupboardTracker.Domain;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Domain.Tests;

public sealed class StockRulesTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Product = Guid.NewGuid();

    private static Unit U(string id, DateTimeOffset checkedIn, DateOnly? expiry = null, UnitStatus status = UnitStatus.InStock) =>
        new(Guid.Parse(id), Product, "5000354922831", status, null, null, checkedIn, null, null, null, expiry);

    [Fact]
    public void Soonest_estimated_expiry_comes_first_and_units_with_an_estimate_before_those_without()
    {
        var noEstimateOldest = U("00000000-0000-0000-0000-000000000001", Day1);
        var expiresLater = U("00000000-0000-0000-0000-000000000002", Day1.AddDays(1), new DateOnly(2026, 12, 1));
        var expiresSoonest = U("00000000-0000-0000-0000-000000000003", Day1.AddDays(2), new DateOnly(2026, 11, 1));

        var order = UnitSelection.InCheckOutOrder([noEstimateOldest, expiresLater, expiresSoonest]);

        Assert.Equal([expiresSoonest, expiresLater, noEstimateOldest], order);
    }

    [Fact]
    public void Without_estimates_it_is_oldest_check_in_first()
    {
        var newer = U("00000000-0000-0000-0000-000000000001", Day1.AddDays(3));
        var oldest = U("00000000-0000-0000-0000-000000000002", Day1);
        Assert.Equal(oldest, UnitSelection.InCheckOutOrder([newer, oldest])[0]);
    }

    [Fact]
    public void Ties_break_on_lowest_id()
    {
        var b = U("00000000-0000-0000-0000-00000000000b", Day1);
        var a = U("00000000-0000-0000-0000-00000000000a", Day1);
        Assert.Equal([a, b], UnitSelection.InCheckOutOrder([b, a]));
    }

    [Fact]
    public void Checked_out_units_are_never_candidates()
    {
        var gone = U("00000000-0000-0000-0000-000000000001", Day1, status: UnitStatus.CheckedOut);
        Assert.Empty(UnitSelection.InCheckOutOrder([gone]));
    }

    [Theory]
    [InlineData("2012345678903", true)]  // EAN-13 starting 20
    [InlineData("2912345678906", true)]  // EAN-13 starting 29
    [InlineData("20590093", false)]      // Lidl EAN-8 starting 2: a normal product (spike #28)
    [InlineData("20055363", false)]
    [InlineData("201234567899", false)]  // UPC-A starting 2: never a store label
    [InlineData("5000354922831", false)]
    public void Store_labels_are_ean13_starting_20_to_29_only(string value, bool isStoreLabel)
    {
        Assert.True(Barcode.TryCreate(value, out var barcode, out var error), error);
        Assert.Equal(isStoreLabel, StoreLabels.IsStoreLabel(barcode!));
    }
}
