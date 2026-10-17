using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Products;

/// <summary>
/// Product lookup: the product cache first, then Open Food Facts. Known and noMatch products are final (never
/// re-fetched or overwritten); lookupFailed products are retried. See docs/data-model.md (Change-feed flow).
/// </summary>
public sealed partial class ProductResolver(
    IProductRepository products, IProductLookup lookup, TimeProvider clock, ILogger<ProductResolver> logger)
{
    public async Task<Product> ResolveAsync(string barcode, CancellationToken cancellationToken)
    {
        // Claim the barcode first, so two scans of a new barcode at once share one product.
        var productId = await products.GetProductIdForBarcodeAsync(barcode, cancellationToken)
            ?? await products.ClaimBarcodeAsync(barcode, Guid.NewGuid(), cancellationToken);

        var existing = await products.GetAsync(productId, cancellationToken);
        return existing switch
        {
            { Status: ProductStatus.LookupFailed } => await RetryAsync(existing, LookupBudget.CheckIn, cancellationToken),
            not null => existing, // known or noMatch: no Open Food Facts call
            // A claim with no product yet: new barcode, or the race winner hasn't stored it yet. Either way, look up.
            null => await products.CreateIfNotExistsAsync(
                await LookUpAsync(productId, barcode, attempts: 0, failedSince: null, LookupBudget.CheckIn, cancellationToken),
                cancellationToken),
        };
    }

    /// <summary>Retries a lookupFailed product. Returns the stored result (which may still be lookupFailed).</summary>
    public async Task<Product> RetryAsync(Product failed, LookupBudget budget, CancellationToken cancellationToken)
    {
        var updated = await LookUpAsync(
            failed.ProductId, failed.Barcodes[0], failed.LookupAttempts, failed.LookupFailedSince, budget, cancellationToken);
        if (await products.TryReplaceLookupFailedAsync(updated, cancellationToken))
        {
            return updated;
        }

        // Someone else resolved it meanwhile (another retry, or naming): use theirs.
        return await products.GetAsync(failed.ProductId, cancellationToken) ?? updated;
    }

    private async Task<Product> LookUpAsync(
        Guid productId, string barcode, int attempts, DateTimeOffset? failedSince, LookupBudget budget,
        CancellationToken cancellationToken)
    {
        var result = await lookup.LookupAsync(barcode, budget, cancellationToken);
        var now = clock.GetUtcNow();
        switch (result)
        {
            case ProductLookupResult.Found found:
                LogFound(barcode, found.Details.DisplayName);
                return Product.Found(productId, barcode, found.Details, now, attempts + 1);
            case ProductLookupResult.NotFound:
                LogNoMatch(barcode);
                return Product.NotFound(productId, barcode, now, attempts + 1);
            case ProductLookupResult.Transient transient:
                LogLookupFailed(barcode, transient.Reason);
                return Product.Failed(productId, barcode, now, attempts + 1, failedSince);
            default:
                throw new InvalidOperationException($"Unexpected lookup result {result}.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Open Food Facts found {Barcode}: {DisplayName}.")]
    private partial void LogFound(string barcode, string displayName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Open Food Facts doesn't know {Barcode}: no-match.")]
    private partial void LogNoMatch(string barcode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Product lookup for {Barcode} failed ({Reason}); it will be retried.")]
    private partial void LogLookupFailed(string barcode, string reason);
}
