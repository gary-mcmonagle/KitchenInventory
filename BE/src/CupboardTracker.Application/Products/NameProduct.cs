using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Products;

public sealed record NameProductCommand(string? Name, string? Brand, string? Size);

/// <summary>Name, brand and size as typed, trimmed and checked: the same rules for naming (#51) and editing (#53).</summary>
public sealed record ProductDetailsInput(string Name, string? Brand, string? Size)
{
    public const int MaxNameLength = 100;
    public const int MaxDetailLength = 60;

    /// <summary>The cleaned-up input, or null if <see cref="Errors"/> has anything.</summary>
    public static ProductDetailsInput? Validate(NameProductCommand command) =>
        Errors(command).Count == 0 ? new(command.Name!.Trim(), Optional(command.Brand), Optional(command.Size)) : null;

    /// <summary>Field → message.</summary>
    public static IReadOnlyDictionary<string, string> Errors(NameProductCommand command)
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

        return errors;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

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
public sealed partial class NameProduct(
    IProductRepository products, ScanFeedNotifier feed, TimeProvider clock, ILogger<NameProduct> logger)
{
    public const int MaxNameLength = ProductDetailsInput.MaxNameLength;
    public const int MaxDetailLength = ProductDetailsInput.MaxDetailLength;

    public async Task<NameProductResult> ExecuteAsync(
        Guid productId, NameProductCommand command, CurrentUser user, CancellationToken cancellationToken)
    {
        if (ProductDetailsInput.Validate(command) is not { } input)
        {
            return new NameProductResult.Invalid(ProductDetailsInput.Errors(command));
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

        var named = product.NamedByUser(input.Name, input.Brand, input.Size, user.ObjectId, clock.GetUtcNow());
        if (!await products.TryReplaceUnnamedAsync(named, cancellationToken))
        {
            var latest = await products.GetAsync(productId, cancellationToken);
            return new NameProductResult.AlreadyNamed(productId, latest?.DisplayName ?? input.Name);
        }

        LogNamed(productId, user.ObjectId);
        await feed.NotifyAsync(new ProductNamed(productId, input.Name, clock.GetUtcNow()), cancellationToken);
        return new NameProductResult.Named(productId, input.Name);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Product {ProductId} named in the app by user {UserId}.")]
    private partial void LogNamed(Guid productId, Guid userId);
}
