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

    /// <summary>A product and its version (ETag), for edits in item detail (#53).</summary>
    Task<Versioned<Product>?> GetVersionedAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Replaces the product only if it hasn't changed since it was read (ETag). False on a conflict.</summary>
    Task<bool> TryReplaceAsync(Versioned<Product> current, Product updated, CancellationToken cancellationToken);

    /// <summary>Known products with no shelf life yet (#52).</summary>
    Task<IReadOnlyList<Product>> GetNeedingShelfLifeAsync(int max, CancellationToken cancellationToken);

    /// <summary>Stores a shelf life, only if the product is still known with none (ETag-guarded). False otherwise.</summary>
    Task<bool> TrySetShelfLifeAsync(Product updated, CancellationToken cancellationToken);

    /// <summary>lookupFailed products, oldest last attempt first.</summary>
    Task<IReadOnlyList<Product>> GetLookupFailedAsync(int max, CancellationToken cancellationToken);
}
