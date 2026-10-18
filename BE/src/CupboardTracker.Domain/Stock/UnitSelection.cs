namespace CupboardTracker.Domain.Stock;

/// <summary>
/// Which in-stock unit a check-out takes (docs/data-model.md, confirmed by Gary): <b>opened units first</b> and units
/// <b>in the freezer last</b> (#65); then soonest estimated expiry (units with an estimate before units without), then
/// oldest <c>checkedInAt</c> (FIFO), then lowest id. The scanner and the app use the same rule.
/// </summary>
public static class UnitSelection
{
    public static IReadOnlyList<Unit> InCheckOutOrder(IEnumerable<Unit> inStock) =>
        [.. inStock
            .Where(u => u.Status == UnitStatus.InStock)
            .OrderBy(Rank) // opened first, frozen last (#65)
            .ThenBy(u => u.EstimatedExpiryDate is null)
            .ThenBy(u => u.EstimatedExpiryDate)
            .ThenBy(u => u.CheckedInAt)
            .ThenBy(u => u.UnitId.ToString("D"), StringComparer.Ordinal)];

    private static int Rank(Unit unit) => unit.IsFrozen ? 2 : unit.IsOpened ? 0 : 1;
}
