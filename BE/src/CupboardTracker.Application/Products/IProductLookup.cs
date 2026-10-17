using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Products;

/// <summary>Who's calling product lookup: each has its own share of the Open Food Facts rate budget.</summary>
public enum LookupBudget
{
    /// <summary>Check-in processing (the change-feed processor).</summary>
    CheckIn,

    /// <summary>The lookup-retry timer.</summary>
    Retry,
}

public abstract record ProductLookupResult
{
    public sealed record Found(ProductDetails Details) : ProductLookupResult;

    /// <summary>The barcode isn't known (or has no usable name): a final no-match.</summary>
    public sealed record NotFound : ProductLookupResult;

    /// <summary>Couldn't get an answer (rate-limited, down, timed out) after retries: try again later.</summary>
    public sealed record Transient(string Reason) : ProductLookupResult;
}

/// <summary>Resolves a barcode to product details (Open Food Facts). Waits for its rate budget rather than failing.</summary>
public interface IProductLookup
{
    Task<ProductLookupResult> LookupAsync(string barcode, LookupBudget budget, CancellationToken cancellationToken);
}
