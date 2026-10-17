using CupboardTracker.Application.Scans;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Products;

/// <summary>A product for the naming screen (#51) and later item detail.</summary>
public sealed record ProductView(
    Guid ProductId,
    string? Barcode,
    ProductStatus Status,
    string DisplayName,
    string? Brand,
    string? Size,
    int InStockCount,
    DateTimeOffset? LatestCheckedInAt,
    Role? LatestDeviceRole);

public sealed class GetProduct(IProductRepository products, IUnitRepository units, IDeviceRepository devices)
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
            role);
    }
}
