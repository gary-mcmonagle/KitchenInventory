using CupboardTracker.Application.Scans;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Products;

/// <summary>An in-stock unit for item detail (#53).</summary>
public sealed record ProductUnitView(
    Guid UnitId, DateTimeOffset CheckedInAt, DateOnly? EstimatedExpiryDate, ExpirySource? ExpirySource, int? DaysUntilExpiry);

/// <summary>A product for the naming screen (#51) and item detail (#53).</summary>
public sealed record ProductView(
    Guid ProductId,
    string? Barcode,
    ProductStatus Status,
    string DisplayName,
    string? Brand,
    string? Size,
    int InStockCount,
    DateTimeOffset? LatestCheckedInAt,
    Role? LatestDeviceRole,
    IReadOnlyList<ProductUnitView>? Units = null,
    int? ShelfLifeDays = null,
    string? ShelfLifeSource = null)
{
    public IReadOnlyList<ProductUnitView> InStockUnits => Units ?? [];
}

/// <summary>A product with its in-stock units in check-out order (soonest estimated expiry first).</summary>
public sealed class GetProduct(
    IProductRepository products, IUnitRepository units, IDeviceRepository devices, StockSettings settings, TimeProvider clock)
{
    public async Task<ProductView?> ExecuteAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await products.GetAsync(productId, cancellationToken);
        if (product is null)
        {
            return null;
        }

        var inStock = await units.GetInStockAsync(productId, cancellationToken);
        var latest = inStock.Select(v => v.Value).MaxBy(u => u.CheckedInAt);
        Role? role = latest?.CheckInDeviceId is { } deviceId
            ? (await devices.GetAsync(deviceId, cancellationToken))?.Role
            : null;
        return new ProductView(
            product.ProductId,
            product.Barcodes is [var first, ..] ? first : latest?.Barcode,
            product.Status,
            product.DisplayName,
            product.Details?.Brand,
            product.Details?.Size,
            inStock.Count,
            latest?.CheckedInAt,
            role,
            UnitViews(inStock.Select(v => v.Value)),
            product.ShelfLife?.Days,
            product.ShelfLife?.Source);
    }

    private List<ProductUnitView> UnitViews(IEnumerable<Unit> inStock)
    {
        var today = settings.Today(clock.GetUtcNow());
        return UnitSelection.InCheckOutOrder(inStock)
            .Select(u => new ProductUnitView(
                u.UnitId, u.CheckedInAt, u.EstimatedExpiryDate, u.ExpirySource,
                u.EstimatedExpiryDate is { } date ? EstimatedExpiry.DaysUntil(date, today) : null))
            .ToList();
    }
}
