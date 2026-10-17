using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Products;

/// <summary>The product cache: products (id = productId) and barcode lookups (barcode → productId).</summary>
public interface IProductRepository
{
    Task<Guid?> GetProductIdForBarcodeAsync(string barcode, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the barcode for <paramref name="proposedProductId"/> (create-if-not-exists on its barcode lookup).
    /// Returns the product ID that owns the barcode: the proposed one, or the winner of a race.
    /// </summary>
    Task<Guid> ClaimBarcodeAsync(string barcode, Guid proposedProductId, CancellationToken cancellationToken);

    Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Creates the product unless it exists; returns whichever is stored.</summary>
    Task<Product> CreateIfNotExistsAsync(Product product, CancellationToken cancellationToken);

    /// <summary>Replaces a product only if it's still lookupFailed (anything else is final). False if it changed meanwhile.</summary>
    Task<bool> TryReplaceLookupFailedAsync(Product updated, CancellationToken cancellationToken);

    /// <summary>Replaces a product only if it's still unnamed (noMatch or lookupFailed) and unchanged since read
    /// (ETag). False if it was named or looked up meanwhile.</summary>
    Task<bool> TryReplaceUnnamedAsync(Product updated, CancellationToken cancellationToken);

    /// <summary>lookupFailed products, oldest last attempt first.</summary>
    Task<IReadOnlyList<Product>> GetLookupFailedAsync(int max, CancellationToken cancellationToken);
}
