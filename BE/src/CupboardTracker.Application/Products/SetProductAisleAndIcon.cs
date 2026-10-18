using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Products;

public abstract record SetClassificationResult
{
    public sealed record Saved(Guid ProductId, string? Value) : SetClassificationResult;

    public sealed record Invalid(string Error) : SetClassificationResult;

    public sealed record NotFound(Guid ProductId) : SetClassificationResult;

    /// <summary>409: not known yet (name it first), or changed while saving.</summary>
    public sealed record Conflict(Guid ProductId, string Reason) : SetClassificationResult;
}

/// <summary>A product's aisle set by hand in Edit details (#71): manual, never overwritten by Jev.</summary>
public sealed partial class SetProductAisle(IProductRepository products, TimeProvider clock, ILogger<SetProductAisle> logger)
{
    public async Task<SetClassificationResult> ExecuteAsync(Guid productId, string? aisleId, CurrentUser user, CancellationToken cancellationToken)
    {
        if (Aisles.Find(aisleId) is not { } aisle)
        {
            return new SetClassificationResult.Invalid($"Aisle must be one of: {string.Join(", ", Aisles.All.Select(a => a.Id))}.");
        }

        var result = await ProductEdits.ReplaceKnownAsync(products, productId, p => p.WithManualAisle(aisle.Id, clock.GetUtcNow()), cancellationToken);
        if (result is SetClassificationResult.Saved)
        {
            LogSet(productId, aisle.Id, user.ObjectId);
            return new SetClassificationResult.Saved(productId, aisle.Id);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Product {ProductId} put in aisle {Aisle} in the app by user {UserId}.")]
    private partial void LogSet(Guid productId, string aisle, Guid userId);
}

/// <summary>
/// A product's icon set by hand in Edit details (#72): manual, never overwritten by Jev. <c>null</c> ("Let Jev
/// choose") clears it, so the next timer run asks Jev again.
/// </summary>
public sealed partial class SetProductIcon(IProductRepository products, TimeProvider clock, ILogger<SetProductIcon> logger)
{
    public async Task<SetClassificationResult> ExecuteAsync(Guid productId, string? icon, CurrentUser user, CancellationToken cancellationToken)
    {
        if (icon is not null && !ProductIcons.IsKnown(icon))
        {
            return new SetClassificationResult.Invalid("That isn't one of the product icons.");
        }

        var result = await ProductEdits.ReplaceKnownAsync(products, productId, p => p.WithManualIcon(icon, clock.GetUtcNow()), cancellationToken);
        if (result is SetClassificationResult.Saved)
        {
            LogSet(productId, icon ?? "(Jev chooses)", user.ObjectId);
            return new SetClassificationResult.Saved(productId, icon);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Product {ProductId} icon set to {Icon} in the app by user {UserId}.")]
    private partial void LogSet(Guid productId, string icon, Guid userId);
}

internal static class ProductEdits
{
    /// <summary>An ETag-guarded change to a known product.</summary>
    public static async Task<SetClassificationResult> ReplaceKnownAsync(
        IProductRepository products, Guid productId, Func<Product, Product> change, CancellationToken cancellationToken)
    {
        var current = await products.GetVersionedAsync(productId, cancellationToken);
        if (current is null)
        {
            return new SetClassificationResult.NotFound(productId);
        }

        if (current.Value.Status != ProductStatus.Known)
        {
            return new SetClassificationResult.Conflict(productId, "This product has no name yet: name it first.");
        }

        return await products.TryReplaceAsync(current, change(current.Value), cancellationToken)
            ? new SetClassificationResult.Saved(productId, null)
            : new SetClassificationResult.Conflict(productId, "This product changed while saving. Try again.");
    }
}
