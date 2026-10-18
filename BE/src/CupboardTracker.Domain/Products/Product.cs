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
    DateTimeOffset? LookupFailedSince,
    DateTimeOffset? NamedAt = null,
    Guid? NamedBy = null,
    ShelfLife? ShelfLife = null,
    DateTimeOffset? EditedAt = null,
    Guid? EditedBy = null,
    DateTimeOffset? FeedDismissedAt = null)
{
    /// <summary>Someone dismissed its amber "No match" toast (#54). Hides the toast only; a newer check-in shows it again.</summary>
    public Product DismissedFromFeed(DateTimeOffset at) => this with { FeedDismissedAt = at };

    /// <summary>Known but not yet estimated: the shelf-life timer picks it up (#52). Unknown items get no shelf life.</summary>
    public bool NeedsShelfLife => Status == ProductStatus.Known && ShelfLife is null;

    public Product WithShelfLife(ShelfLife shelfLife) => this with { ShelfLife = shelfLife };

    /// <summary>Unnamed: Open Food Facts had no answer (yet). Shown as "Unknown item" and waiting for a name.</summary>
    public bool IsUnnamed => Status is ProductStatus.NoMatch or ProductStatus.LookupFailed;

    /// <summary>Named by a person in the app (#51): known from now on, so check-ins never look it up again.</summary>
    public Product NamedByUser(string name, string? brand, string? size, Guid userId, DateTimeOffset at) =>
        this with
        {
            Status = ProductStatus.Known,
            Source = ProductSource.Manual,
            DisplayName = name,
            Details = new ProductDetails(name, brand, size, Details?.ImageUrl, Details?.CategoryTags ?? []),
            LookupFailedSince = null,
            NamedAt = at,
            NamedBy = userId,
        };

    /// <summary>A known product's details corrected in item detail (#53): manual from now on, so nothing (Open Food
    /// Facts, Jev) overwrites the name. Its shelf life is kept.</summary>
    public Product EditedByUser(string name, string? brand, string? size, Guid userId, DateTimeOffset at) =>
        this with
        {
            Source = ProductSource.Manual,
            DisplayName = name,
            Details = new ProductDetails(name, brand, size, Details?.ImageUrl, Details?.CategoryTags ?? []),
            EditedAt = at,
            EditedBy = userId,
        };

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
