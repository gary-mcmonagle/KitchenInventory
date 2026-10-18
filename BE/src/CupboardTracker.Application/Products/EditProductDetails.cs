using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Products;

public abstract record EditProductDetailsResult
{
    public sealed record Saved(Guid ProductId, string DisplayName, string? Brand, string? Size) : EditProductDetailsResult;

    /// <summary>400: field → message.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string> Errors) : EditProductDetailsResult;

    public sealed record NotFound(Guid ProductId) : EditProductDetailsResult;

    /// <summary>409: the product is still unknown (name it instead), or changed while saving.</summary>
    public sealed record Conflict(Guid ProductId, string Reason) : EditProductDetailsResult;
}

/// <summary>
/// Corrects a known product's name, brand and size in item detail (#53). The product becomes <c>source: manual</c>
/// with <c>editedAt</c> / <c>editedBy</c>; known products are never looked up again, so nothing overwrites it.
/// Unknown items are named with <see cref="NameProduct"/> instead.
/// </summary>
public sealed partial class EditProductDetails(IProductRepository products, TimeProvider clock, ILogger<EditProductDetails> logger)
{
    public async Task<EditProductDetailsResult> ExecuteAsync(
        Guid productId, NameProductCommand command, CurrentUser user, CancellationToken cancellationToken)
    {
        if (ProductDetailsInput.Validate(command) is not { } input)
        {
            return new EditProductDetailsResult.Invalid(ProductDetailsInput.Errors(command));
        }

        var current = await products.GetVersionedAsync(productId, cancellationToken);
        if (current is null)
        {
            return new EditProductDetailsResult.NotFound(productId);
        }

        if (current.Value.Status != ProductStatus.Known)
        {
            return new EditProductDetailsResult.Conflict(productId, "This product has no name yet: name it instead.");
        }

        var edited = current.Value.EditedByUser(input.Name, input.Brand, input.Size, user.ObjectId, clock.GetUtcNow());
        if (!await products.TryReplaceAsync(current, edited, cancellationToken))
        {
            return new EditProductDetailsResult.Conflict(productId, "This product changed while saving. Try again.");
        }

        LogEdited(productId, user.ObjectId);
        return new EditProductDetailsResult.Saved(productId, input.Name, input.Brand, input.Size);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Product {ProductId} details edited in the app by user {UserId}.")]
    private partial void LogEdited(Guid productId, Guid userId);
}
