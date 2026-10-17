namespace CupboardTracker.Domain.Stock;

/// <summary>
/// Which in-stock unit a check-out takes (docs/data-model.md, confirmed by Gary): soonest estimated expiry first
/// (units with an estimate before units without), then oldest <c>checkedInAt</c> (FIFO), then lowest id.
/// </summary>
public static class UnitSelection
{
    public static IReadOnlyList<Unit> InCheckOutOrder(IEnumerable<Unit> inStock) =>
        [.. inStock
            .Where(u => u.Status == UnitStatus.InStock)
            .OrderBy(u => u.EstimatedExpiryDate is null)
            .ThenBy(u => u.EstimatedExpiryDate)
            .ThenBy(u => u.CheckedInAt)
            .ThenBy(u => u.UnitId.ToString("D"), StringComparer.Ordinal)];
}
