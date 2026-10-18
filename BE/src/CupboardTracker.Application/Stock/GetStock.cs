using CupboardTracker.Application.Products;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Stock;

/// <summary>An in-stock unit as the stock list needs it.</summary>
public sealed record StockUnit(
    Guid ProductId,
    string? Barcode,
    DateTimeOffset CheckedInAt,
    DateOnly? EstimatedExpiryDate = null,
    ExpirySource? ExpirySource = null,
    DateOnly? OpenedOn = null,
    DateOnly? FrozenOn = null,
    DateOnly? DefrostedOn = null)
{
    public bool Opened => OpenedOn is not null;

    public bool Frozen => FrozenOn is not null && DefrostedOn is null;

    /// <summary>When its estimate is from being opened, frozen or defrosted (#65): which, and since when.</summary>
    public (string State, DateOnly On)? EstimateState => ExpirySource switch
    {
        Domain.Stock.ExpirySource.Opened when OpenedOn is { } on => ("opened", on),
        Domain.Stock.ExpirySource.Frozen when FrozenOn is { } on => ("frozen", on),
        Domain.Stock.ExpirySource.Defrosted when DefrostedOn is { } on => ("defrosted", on),
        _ => null,
    };
}

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

/// <summary>Which part of the stock list a product is in (#52).</summary>
public enum StockSection
{
    /// <summary>Its soonest unit is estimated to be expired or due within <see cref="StockSettings.UseFirstDays"/>.</summary>
    UseFirst,

    /// <summary>Everything else that's named, including products with no estimate yet.</summary>
    Later,

    /// <summary>Unknown items (no match, or lookup failed).</summary>
    WaitingForName,
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
    StockProductStatus Status,
    StockSection Section = StockSection.Later,
    DateOnly? SoonestEstimatedExpiryDate = null,
    int? DaysUntilSoonest = null,
    int? ShelfLifeDays = null,
    int OpenedCount = 0,
    int FrozenCount = 0,
    string? SoonestState = null,
    DateOnly? SoonestStateOn = null);

public sealed record StockView(
    DateTimeOffset GeneratedAt, int TotalUnits, IReadOnlyList<StockProduct> Products, int UseFirstCount = 0);

/// <summary>
/// The stock list (#49, #52): in-stock units grouped by product, with counts, already sorted and put in sections so
/// the UI does no aggregation. Use first (soonest estimated expiry within <see cref="StockSettings.UseFirstDays"/>,
/// or passed) and Later are sorted by soonest estimated expiry (no estimate last), then name ignoring case; Waiting
/// for a name (no match, or lookup failed) by most recently scanned.
/// </summary>
public sealed class GetStock(IStockQueries queries, IProductRepository products, StockSettings settings, TimeProvider clock)
{
    public async Task<StockView> ExecuteAsync(CancellationToken cancellationToken)
    {
        var units = await queries.GetInStockUnitsAsync(cancellationToken);
        var groups = units.GroupBy(u => u.ProductId).ToList();
        // Point reads, all at once: one after another took about a second for 26 products.
        var found = await Task.WhenAll(groups.Select(g => products.GetAsync(g.Key, cancellationToken)));
        var now = clock.GetUtcNow();
        var today = settings.Today(now);
        var rows = new List<StockProduct>();
        foreach (var (group, product) in groups.Zip(found))
        {
            var latest = group.MaxBy(u => u.CheckedInAt)!;
            var barcode = product?.Barcodes is [var first, ..] ? first : latest.Barcode;
            var status = StatusOf(product);
            var soonest = group.Min(u => u.EstimatedExpiryDate);
            var soonestState = group.Where(u => u.EstimatedExpiryDate is not null).MinBy(u => u.EstimatedExpiryDate)?.EstimateState;
            int? days = soonest is { } date ? EstimatedExpiry.DaysUntil(date, today) : null;
            var section = status != StockProductStatus.Known ? StockSection.WaitingForName
                : settings.IsUseFirst(days) ? StockSection.UseFirst
                : StockSection.Later;
            rows.Add(new StockProduct(
                group.Key,
                barcode,
                product?.DisplayName ?? Product.UnknownName(barcode ?? "no barcode"),
                product?.Details?.Brand,
                product?.Details?.Size,
                group.Count(),
                latest.CheckedInAt,
                status,
                section,
                soonest,
                days,
                product?.ShelfLife?.Days,
                group.Count(u => u.Opened),
                group.Count(u => u.Frozen),
                soonestState?.State,
                soonestState?.On));
        }

        var sorted = BySoonest(rows.Where(r => r.Section == StockSection.UseFirst))
            .Concat(BySoonest(rows.Where(r => r.Section == StockSection.Later)))
            .Concat(rows.Where(r => r.Section == StockSection.WaitingForName).OrderByDescending(r => r.LatestCheckedInAt))
            .ToList();
        return new StockView(now, units.Count, sorted, sorted.Count(r => r.Section == StockSection.UseFirst));
    }

    private static IEnumerable<StockProduct> BySoonest(IEnumerable<StockProduct> rows) =>
        rows.OrderBy(r => r.SoonestEstimatedExpiryDate is null)
            .ThenBy(r => r.SoonestEstimatedExpiryDate)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.DisplayName, StringComparer.Ordinal);

    private static StockProductStatus StatusOf(Product? product) => product?.Status switch
    {
        ProductStatus.Known => StockProductStatus.Known,
        ProductStatus.NoMatch => StockProductStatus.NoMatch,
        // A missing product document shouldn't happen; treat it as a lookup still to be done.
        _ => StockProductStatus.LookupFailed,
    };
}
