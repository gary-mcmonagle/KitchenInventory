using CupboardTracker.Application.Scans;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Products;

/// <summary>An in-stock unit for item detail (#53), with its state (#65).</summary>
public sealed record ProductUnitView(
    Guid UnitId,
    DateTimeOffset CheckedInAt,
    DateOnly? EstimatedExpiryDate,
    ExpirySource? ExpirySource,
    int? DaysUntilExpiry,
    DateOnly? OpenedOn = null,
    DateOnly? FrozenOn = null,
    DateOnly? DefrostedOn = null,
    Storage? CurrentStorage = null,
    bool EstimatePending = false,
    bool CanUndoStateChange = false)
{
    public bool IsFrozen => FrozenOn is not null && DefrostedOn is null;

    public static ProductUnitView From(Unit unit, DateOnly today) =>
        new(unit.UnitId, unit.CheckedInAt, unit.EstimatedExpiryDate, unit.ExpirySource,
            unit.EstimatedExpiryDate is { } date ? EstimatedExpiry.DaysUntil(date, today) : null,
            unit.OpenedOn, unit.FrozenOn, unit.DefrostedOn, unit.CurrentStorage, unit.EstimatePending,
            unit.BeforeStateChange is not null);
}

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
    string? ShelfLifeSource = null,
    StateShelfLife? Opened = null,
    StateShelfLife? Frozen = null,
    StateShelfLife? Defrosted = null,
    Storage? UsualStorage = null)
{
    public int OpenedCount => InStockUnits.Count(u => u.OpenedOn is not null);

    public int FrozenCount => InStockUnits.Count(u => u.IsFrozen);

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
            product.ShelfLife?.Source,
            product.States.Opened,
            product.States.Frozen,
            product.States.Defrosted,
            product.ShelfLife?.UsualStorage);
    }

    private List<ProductUnitView> UnitViews(IEnumerable<Unit> inStock)
    {
        var today = settings.Today(clock.GetUtcNow());
        return UnitSelection.InCheckOutOrder(inStock).Select(u => ProductUnitView.From(u, today)).ToList();
    }
}
