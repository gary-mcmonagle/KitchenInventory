namespace CupboardTracker.Domain.Products;

/// <summary>Whether product lookup has a final answer. See docs/data-model.md.</summary>
public enum ProductStatus
{
    /// <summary>We have a name (from Open Food Facts, naming or photo identification).</summary>
    Known,

    /// <summary>Open Food Facts doesn't know this barcode. Final: never looked up again.</summary>
    NoMatch,

    /// <summary>Open Food Facts couldn't be reached. Retried by the timer and on the next check-in.</summary>
    LookupFailed,
}

/// <summary>Where a known product's name came from.</summary>
public enum ProductSource
{
    OpenFoodFacts,
    Manual,
    Photo,
}

/// <summary>Details found by product lookup.</summary>
public sealed record ProductDetails(
    string DisplayName, string? Brand, string? Size, string? ImageUrl, IReadOnlyList<string> CategoryTags);

/// <summary>A kind of item, with its own <see cref="ProductId"/>; barcodes are optional lookups.</summary>
public sealed record Product(
    Guid ProductId,
    ProductStatus Status,
    ProductSource? Source,
    IReadOnlyList<string> Barcodes,
    string DisplayName,
    ProductDetails? Details,
    DateTimeOffset? FetchedAt,
    int LookupAttempts,
    DateTimeOffset? LastLookupAttemptAt,
    DateTimeOffset? LookupFailedSince)
{
    public static string UnknownName(string barcode) => $"Unknown item ({barcode})";

    public static Product Found(Guid productId, string barcode, ProductDetails details, DateTimeOffset at, int attempts) =>
        new(productId, ProductStatus.Known, ProductSource.OpenFoodFacts, [barcode], details.DisplayName, details,
            at, attempts, at, null);

    public static Product NotFound(Guid productId, string barcode, DateTimeOffset at, int attempts) =>
        new(productId, ProductStatus.NoMatch, null, [barcode], UnknownName(barcode), null, null, attempts, at, null);

    public static Product Failed(Guid productId, string barcode, DateTimeOffset at, int attempts, DateTimeOffset? since) =>
        new(productId, ProductStatus.LookupFailed, null, [barcode], UnknownName(barcode), null, null, attempts, at,
            since ?? at);
}
