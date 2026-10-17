using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Products;

public sealed record NameProductCommand(string? Name, string? Brand, string? Size);

public abstract record NameProductResult
{
    public sealed record Named(Guid ProductId, string DisplayName) : NameProductResult;

    /// <summary>400: field → message.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string> Errors) : NameProductResult;

    public sealed record NotFound(Guid ProductId) : NameProductResult;

    /// <summary>409: the product already has a name (renaming known products isn't this feature).</summary>
    public sealed record AlreadyNamed(Guid ProductId, string DisplayName) : NameProductResult;
}

/// <summary>
/// Names an unknown item (#51): a no-match or lookup-failed product becomes known, with source <c>manual</c>, so
/// every unit shows the name and future check-ins never look it up. Only applies while the product is still unnamed:
/// the lookup-retry timer and two people naming at once can't overwrite each other.
/// </summary>
public sealed partial class NameProduct(IProductRepository products, TimeProvider clock, ILogger<NameProduct> logger)
{
    public const int MaxNameLength = 100;
    public const int MaxDetailLength = 60;

    public async Task<NameProductResult> ExecuteAsync(
        Guid productId, NameProductCommand command, CurrentUser user, CancellationToken cancellationToken)
    {
        var name = command.Name?.Trim() ?? "";
        var brand = Optional(command.Brand);
        var size = Optional(command.Size);
        var errors = new Dictionary<string, string>();
        if (name.Length == 0)
        {
            errors["name"] = "Enter a name.";
        }
        else if (name.Length > MaxNameLength)
        {
            errors["name"] = $"Keep the name to {MaxNameLength} characters or fewer.";
        }

        if (brand?.Length > MaxDetailLength)
        {
            errors["brand"] = $"Keep the brand to {MaxDetailLength} characters or fewer.";
        }

        if (size?.Length > MaxDetailLength)
        {
            errors["size"] = $"Keep the size to {MaxDetailLength} characters or fewer.";
        }

        if (errors.Count > 0)
        {
            return new NameProductResult.Invalid(errors);
        }

        var product = await products.GetAsync(productId, cancellationToken);
        if (product is null)
        {
            return new NameProductResult.NotFound(productId);
        }

        if (!product.IsUnnamed)
        {
            return new NameProductResult.AlreadyNamed(productId, product.DisplayName);
        }

        var named = product.NamedByUser(name, brand, size, user.ObjectId, clock.GetUtcNow());
        if (!await products.TryReplaceUnnamedAsync(named, cancellationToken))
        {
            var latest = await products.GetAsync(productId, cancellationToken);
            return new NameProductResult.AlreadyNamed(productId, latest?.DisplayName ?? name);
        }

        LogNamed(productId, user.ObjectId);
        return new NameProductResult.Named(productId, name);
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [LoggerMessage(Level = LogLevel.Information, Message = "Product {ProductId} named in the app by user {UserId}.")]
    private partial void LogNamed(Guid productId, Guid userId);
}
