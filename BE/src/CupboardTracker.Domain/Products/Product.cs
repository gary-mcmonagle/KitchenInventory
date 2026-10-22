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
    DateTimeOffset? FeedDismissedAt = null,
    DateTimeOffset? LastAddedAt = null,
    ProductStateEstimates? StateEstimates = null,
    ProductAisle? Aisle = null,
    ProductIcon? Icon = null,
    ProductGenericName? Generic = null,
    ProductAnimalContent? Animal = null)
{
    /// <summary>Known, with an aisle (step 1 needs it), and no animal content yet (#112): the estimate timer works it out.</summary>
    public bool NeedsAnimalContent => Status == ProductStatus.Known && Aisle is not null && Animal is null;

    /// <summary>Set by hand in Edit details (#112): never overwritten.</summary>
    public Product WithManualAnimalContent(AnimalContent value, DateTimeOffset at) =>
        this with { Animal = new ProductAnimalContent(value, AnimalContents.ManualSource, null, at) };

    /// <summary>Known, with an aisle, and no generic name yet (#90): the estimate timer works one out.</summary>
    public bool NeedsGenericName => Status == ProductStatus.Known && Aisle is not null && Generic is null;

    /// <summary>A generic name chosen in Edit details (#90): never overwritten.</summary>
    public Product WithManualGenericName(GenericName name, DateTimeOffset at) =>
        this with { Generic = new ProductGenericName(name.Id, name.Name, GenericNameSource.Manual, null, at) };

    /// <summary>Known, and Jev hasn't given it an aisle or an icon yet (#71, #72): one request asks for both.</summary>
    public bool NeedsClassification => Status == ProductStatus.Known && (Aisle is null || Icon is null);

    /// <summary>An aisle chosen in Edit details (#71): Jev never overwrites it.</summary>
    public Product WithManualAisle(string aisleId, DateTimeOffset at) =>
        this with { Aisle = new ProductAisle(aisleId, ShelfLife.ManualSource, 1, at) };

    /// <summary>An icon chosen in Edit details (#72), or <c>null</c> to let Jev choose again on the next run.</summary>
    public Product WithManualIcon(string? icon, DateTimeOffset at) =>
        this with { Icon = icon is null ? null : new ProductIcon(icon, ShelfLife.ManualSource, 1, at) };

    /// <summary>Has a shelf life but Jev hasn't answered the opened / frozen / defrosted questions yet (#65).</summary>
    public bool NeedsStateEstimates => Status == ProductStatus.Known && ShelfLife is not null && StateEstimates?.EstimatedAt is null;

    public ProductStateEstimates States => StateEstimates ?? ProductStateEstimates.None;

    /// <summary>"Once opened" set by hand in item detail (#65): kept where Jev would keep it, else the usual storage.</summary>
    public Product WithManualOpenedShelfLife(int days) =>
        this with
        {
            StateEstimates = States with
            {
                Opened = StateShelfLife.Manual(days, States.Opened?.Storage ?? ShelfLife?.UsualStorage ?? Storage.Cupboard),
            },
        };

    /// <summary>A product with no barcode: fruit, veg, bakery, deli (#58). Added from the app, never scanned.</summary>
    public bool IsLoose => Barcodes.Count == 0;

    /// <summary>Its name as matched when adding from the app: lower case, single spaces, trimmed.</summary>
    public string NameKey => NameKeyOf(DisplayName);

    public static string NameKeyOf(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>A new loose product (#58): known at once, no barcode. <paramref name="source"/> is
    /// <see cref="ProductSource.Photo"/> for a recognised item, else <see cref="ProductSource.Manual"/>.</summary>
    public static Product Loose(
        Guid productId, string name, string? brand, string? size, ProductSource source, Guid userId, DateTimeOffset at) =>
        new(productId, ProductStatus.Known, source, [], name, new ProductDetails(name, brand, size, null, []),
            null, 0, null, null, at, userId);

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
