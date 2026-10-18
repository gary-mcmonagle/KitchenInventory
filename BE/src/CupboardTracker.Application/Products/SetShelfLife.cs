using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Products;

public abstract record SetShelfLifeResult
{
    public sealed record Saved(Guid ProductId, int Days) : SetShelfLifeResult;

    public sealed record Invalid(string Error) : SetShelfLifeResult;

    public sealed record NotFound(Guid ProductId) : SetShelfLifeResult;

    /// <summary>409: the product is unknown (no shelf life until it's named), or changed while saving.</summary>
    public sealed record Conflict(Guid ProductId, string Reason) : SetShelfLifeResult;
}

/// <summary>
/// Sets a product's shelf life by hand in item detail (#53): <c>shelfLifeSource: manual</c>, which Jev never
/// overwrites. It applies to units bought from now on; units that already have a date keep it.
/// </summary>
public sealed partial class SetShelfLife(IProductRepository products, TimeProvider clock, ILogger<SetShelfLife> logger)
{
    public const int MinDays = 1;
    public const int MaxDays = 3650;

    public async Task<SetShelfLifeResult> ExecuteAsync(Guid productId, int? days, CurrentUser user, CancellationToken cancellationToken)
    {
        if (days is not (>= MinDays and <= MaxDays))
        {
            return new SetShelfLifeResult.Invalid($"Days must be a whole number from {MinDays} to {MaxDays}.");
        }

        var current = await products.GetVersionedAsync(productId, cancellationToken);
        if (current is null)
        {
            return new SetShelfLifeResult.NotFound(productId);
        }

        if (current.Value.Status != ProductStatus.Known)
        {
            return new SetShelfLifeResult.Conflict(productId, "This product has no name yet: name it first.");
        }

        var storage = current.Value.ShelfLife?.UsualStorage ?? Storage.Cupboard;
        var updated = current.Value.WithShelfLife(ShelfLife.Manual(days.Value, storage, clock.GetUtcNow()));
        if (!await products.TryReplaceAsync(current, updated, cancellationToken))
        {
            return new SetShelfLifeResult.Conflict(productId, "This product changed while saving. Try again.");
        }

        LogSet(productId, days.Value, user.ObjectId);
        return new SetShelfLifeResult.Saved(productId, days.Value);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Product {ProductId} shelf life set to {Days} days in the app by user {UserId}.")]
    private partial void LogSet(Guid productId, int days, Guid userId);
}

/// <summary>
/// Sets a product's "once opened" shelf life by hand in item detail (#65): manual, which Jev never overwrites. It
/// applies to units opened from now on; units already opened keep their date.
/// </summary>
public sealed partial class SetOpenedShelfLife(IProductRepository products, ILogger<SetOpenedShelfLife> logger)
{
    public async Task<SetShelfLifeResult> ExecuteAsync(Guid productId, int? days, CurrentUser user, CancellationToken cancellationToken)
    {
        if (days is not (>= SetShelfLife.MinDays and <= SetShelfLife.MaxDays))
        {
            return new SetShelfLifeResult.Invalid($"Days must be a whole number from {SetShelfLife.MinDays} to {SetShelfLife.MaxDays}.");
        }

        var current = await products.GetVersionedAsync(productId, cancellationToken);
        if (current is null)
        {
            return new SetShelfLifeResult.NotFound(productId);
        }

        if (current.Value.Status != ProductStatus.Known)
        {
            return new SetShelfLifeResult.Conflict(productId, "This product has no name yet: name it first.");
        }

        if (!await products.TryReplaceAsync(current, current.Value.WithManualOpenedShelfLife(days.Value), cancellationToken))
        {
            return new SetShelfLifeResult.Conflict(productId, "This product changed while saving. Try again.");
        }

        LogSet(productId, days.Value, user.ObjectId);
        return new SetShelfLifeResult.Saved(productId, days.Value);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Product {ProductId} \"once opened\" shelf life set to {Days} days in the app by user {UserId}.")]
    private partial void LogSet(Guid productId, int days, Guid userId);
}
