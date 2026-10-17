using CupboardTracker.Application.Products;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Stock;

/// <summary>An in-stock unit as the stock list needs it.</summary>
public sealed record StockUnit(Guid ProductId, string? Barcode, DateTimeOffset CheckedInAt);

/// <summary>Reads for the stock list (cross-partition over inventoryItems; small at household volume).</summary>
public interface IStockQueries
{
    Task<IReadOnlyList<StockUnit>> GetInStockUnitsAsync(CancellationToken cancellationToken);
}

public enum StockProductStatus
{
    Known,
    NoMatch,
    LookupFailed,
}

/// <summary>One product in stock and how many units of it are in the house.</summary>
public sealed record StockProduct(
    Guid ProductId,
    string? Barcode,
    string DisplayName,
    string? Brand,
    string? Size,
    int Count,
    DateTimeOffset LatestCheckedInAt,
    StockProductStatus Status);

public sealed record StockView(DateTimeOffset GeneratedAt, int TotalUnits, IReadOnlyList<StockProduct> Products);

/// <summary>
/// The stock list (#49): in-stock units grouped by product, with counts, already sorted so the UI does no
/// aggregation. Named products first, alphabetical ignoring case; then unnamed ones (no match, or lookup failed),
/// most recently scanned first.
/// </summary>
public sealed class GetStock(IStockQueries queries, IProductRepository products, TimeProvider clock)
{
    public async Task<StockView> ExecuteAsync(CancellationToken cancellationToken)
    {
        var units = await queries.GetInStockUnitsAsync(cancellationToken);
        var groups = units.GroupBy(u => u.ProductId).ToList();
        // Point reads, all at once: one after another took about a second for 26 products.
        var found = await Task.WhenAll(groups.Select(g => products.GetAsync(g.Key, cancellationToken)));
        var rows = new List<StockProduct>();
        foreach (var (group, product) in groups.Zip(found))
        {
            var latest = group.MaxBy(u => u.CheckedInAt)!;
            var barcode = product?.Barcodes is [var first, ..] ? first : latest.Barcode;
            rows.Add(new StockProduct(
                group.Key,
                barcode,
                product?.DisplayName ?? Product.UnknownName(barcode ?? "no barcode"),
                product?.Details?.Brand,
                product?.Details?.Size,
                group.Count(),
                latest.CheckedInAt,
                StatusOf(product)));
        }

        var sorted = rows
            .Where(r => r.Status == StockProductStatus.Known)
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
            .Concat(rows.Where(r => r.Status != StockProductStatus.Known).OrderByDescending(r => r.LatestCheckedInAt))
            .ToList();
        return new StockView(clock.GetUtcNow(), units.Count, sorted);
    }

    private static StockProductStatus StatusOf(Product? product) => product?.Status switch
    {
        ProductStatus.Known => StockProductStatus.Known,
        ProductStatus.NoMatch => StockProductStatus.NoMatch,
        // A missing product document shouldn't happen; treat it as a lookup still to be done.
        _ => StockProductStatus.LookupFailed,
    };
}
