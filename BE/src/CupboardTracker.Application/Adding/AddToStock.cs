using System.Security.Cryptography;
using System.Text;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Adding;

public enum AddSource
{
    Photo,
    Pick,
    Typed,
}

/// <summary>One row to add: an existing product, or a name to match or create.</summary>
public sealed record AddItem(Guid? ProductId, string? Name, string? Brand, string? Size, int Quantity, AddSource Source);

public sealed record AddedItem(Guid ProductId, string DisplayName, int Quantity, bool CreatedProduct);

public abstract record AddToStockResult
{
    public sealed record Added(IReadOnlyList<AddedItem> Items) : AddToStockResult
    {
        public int Units => Items.Sum(i => i.Quantity);
    }

    public sealed record Invalid(IReadOnlyDictionary<string, string> Errors) : AddToStockResult;
}

/// <summary>
/// <c>POST /api/stock/add</c> (#58): puts loose items (or anything) in stock from the app. Each row is an existing
/// product, or a name that's matched to one (<see cref="ProductMatcher"/>) or becomes a new loose product. Units are
/// added now by the user, with an estimated expiry when the product's shelf life is known (otherwise the shelf-life
/// timer fills it in, #52).
/// <para><b>Idempotent by request ID:</b> new products' and units' ids are derived from it, so a double tap or a retry
/// creates nothing twice.</para>
/// </summary>
public sealed partial class AddToStock(
    IProductRepository products,
    IUnitRepository units,
    ProductMatcher matcher,
    StockSettings settings,
    TimeProvider clock,
    ILogger<AddToStock> logger)
{
    public const int MaxItems = 20;
    public const int MaxQuantity = 20;

    public async Task<AddToStockResult> ExecuteAsync(
        Guid requestId, IReadOnlyList<AddItem> items, CurrentUser user, CancellationToken cancellationToken)
    {
        var errors = Validate(requestId, items);
        if (errors.Count > 0)
        {
            return new AddToStockResult.Invalid(errors);
        }

        var now = clock.GetUtcNow();
        var added = new List<AddedItem>();
        for (var row = 0; row < items.Count; row++)
        {
            var item = items[row];
            var (product, created) = await ResolveAsync(item, DeterministicId(requestId, $"product:{row}"), user, now, cancellationToken);
            if (product is null)
            {
                return new AddToStockResult.Invalid(new Dictionary<string, string> { [$"items[{row}].productId"] = "No such product." });
            }

            for (var n = 0; n < item.Quantity; n++)
            {
                var expiry = product.ShelfLife is { } shelfLife
                    ? EstimatedExpiry.For(now, shelfLife.Days, settings.HomeTimeZone)
                    : (DateOnly?)null;
                await units.CreateIfNotExistsAsync(
                    Unit.AddedInApp(DeterministicId(requestId, $"unit:{row}:{n}"), product.ProductId, user.ObjectId, now, expiry),
                    cancellationToken);
            }

            await TouchAsync(product.ProductId, now, cancellationToken);
            added.Add(new AddedItem(product.ProductId, product.DisplayName, item.Quantity, created));
        }

        LogAdded(requestId, added.Sum(a => a.Quantity), added.Count, user.ObjectId);
        return new AddToStockResult.Added(added);
    }

    /// <summary>The product a row means: by id, by name, or a new loose product (id from the request, so a retry finds it).</summary>
    internal async Task<(Product? Product, bool Created)> ResolveAsync(
        AddItem item, Guid newProductId, CurrentUser user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (item.ProductId is { } id)
        {
            return (await products.GetAsync(id, cancellationToken) is { Status: ProductStatus.Known } p ? p : null, false);
        }

        var name = item.Name!.Trim();
        if (await products.GetAsync(newProductId, cancellationToken) is { } fromRetry)
        {
            return (fromRetry, true);
        }

        if (await matcher.FindAsync(name, cancellationToken) is { } match)
        {
            return (match, false);
        }

        var source = item.Source == AddSource.Photo ? ProductSource.Photo : ProductSource.Manual;
        var created = await products.CreateIfNotExistsAsync(
            Product.Loose(newProductId, name, Optional(item.Brand), Optional(item.Size), source, user.ObjectId, now),
            cancellationToken);
        LogCreated(created.ProductId, created.DisplayName);
        return (created, true);
    }

    /// <summary>Remembers when it was last added, for the quick pick's order. Best-effort: a conflict just skips it.</summary>
    private async Task TouchAsync(Guid productId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await products.GetVersionedAsync(productId, cancellationToken) is { } current)
        {
            await products.TryReplaceAsync(current, current.Value with { LastAddedAt = now }, cancellationToken);
        }
    }

    internal static Dictionary<string, string> Validate(Guid requestId, IReadOnlyList<AddItem> items)
    {
        var errors = new Dictionary<string, string>();
        if (requestId == Guid.Empty)
        {
            errors["requestId"] = "Send a request ID (a new UUID per add), so a retry doesn't add twice.";
        }

        if (items.Count is 0 or > MaxItems)
        {
            errors["items"] = $"Add between 1 and {MaxItems} items at a time.";
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.ProductId is null && string.IsNullOrWhiteSpace(item.Name))
            {
                errors[$"items[{i}].name"] = "Enter a name.";
            }
            else if (item.ProductId is null && item.Name!.Trim().Length > ProductDetailsInput.MaxNameLength)
            {
                errors[$"items[{i}].name"] = $"Keep the name to {ProductDetailsInput.MaxNameLength} characters or fewer.";
            }

            if (Optional(item.Brand)?.Length > ProductDetailsInput.MaxDetailLength || Optional(item.Size)?.Length > ProductDetailsInput.MaxDetailLength)
            {
                errors[$"items[{i}].brand"] = $"Keep brand and size to {ProductDetailsInput.MaxDetailLength} characters or fewer.";
            }

            if (item.Quantity is < 1 or > MaxQuantity)
            {
                errors[$"items[{i}].quantity"] = $"Quantity must be 1 to {MaxQuantity}.";
            }
        }

        return errors;
    }

    /// <summary>A stable id from the request and a label (a name-based UUID, version 8 style).</summary>
    public static Guid DeterministicId(Guid requestId, string label)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{requestId:D}/{label}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // version 8 (custom)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [LoggerMessage(Level = LogLevel.Information, Message = "Added {Units} units of {Products} products from the app (request {RequestId}, user {UserId}).")]
    private partial void LogAdded(Guid requestId, int units, int products, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "New loose product {ProductId}: {DisplayName}.")]
    private partial void LogCreated(Guid productId, string displayName);
}
