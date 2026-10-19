using CupboardTracker.Application.Products;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.GenericNaming;

/// <summary>A generic name for the Edit details picker: its aisle and how many products use it.</summary>
public sealed record GenericNameOption(string Id, string Name, string AisleId, string AisleName, int ProductCount);

/// <summary><c>GET /api/generic-names</c> (#90): every generic name, A–Z, for the picker.</summary>
public sealed class ListGenericNames(IGenericNameRepository names, IProductRepository products)
{
    public async Task<IReadOnlyList<GenericNameOption>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var all = await names.ListAsync(cancellationToken);
        var counts = (await products.ListKnownAsync(cancellationToken))
            .Where(p => p.Generic is not null)
            .GroupBy(p => p.Generic!.Id)
            .ToDictionary(g => g.Key, g => g.Count());
        return
        [
            .. all.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Select(n => new GenericNameOption(n.Id, n.Name, n.AisleId, Aisles.Of(n.AisleId).Name, counts.GetValueOrDefault(n.Id))),
        ];
    }
}

public abstract record SetGenericNameResult
{
    public sealed record Saved(Guid ProductId, ProductGenericName Generic, bool Created) : SetGenericNameResult;

    public sealed record Invalid(string Error) : SetGenericNameResult;

    public sealed record NotFound : SetGenericNameResult;

    public sealed record Conflict(string Reason) : SetGenericNameResult;
}

/// <summary>
/// <c>PUT /api/products/{id}/generic-name</c> (#90): Edit details' "Generic name". Either an existing name
/// (<paramref name="genericNameId"/>, the default path, so fixes don't create near-duplicates) or a new one typed in
/// (<paramref name="newName"/>, created in the product's aisle; a name that normalises to an existing record uses it).
/// Saved as <c>manual</c>, which nothing automatic overwrites.
/// </summary>
public sealed partial class SetProductGenericName(
    IProductRepository products, IGenericNameRepository names, TimeProvider clock, ILogger<SetProductGenericName> logger)
{
    public async Task<SetGenericNameResult> ExecuteAsync(
        Guid productId, string? genericNameId, string? newName, CurrentUser user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(genericNameId) == string.IsNullOrWhiteSpace(newName))
        {
            return new SetGenericNameResult.Invalid("Send either genericNameId (an existing name) or name (a new one).");
        }

        if (await products.GetVersionedAsync(productId, cancellationToken) is not { Value.Status: ProductStatus.Known } current)
        {
            return new SetGenericNameResult.NotFound();
        }

        GenericName record;
        var created = false;
        if (!string.IsNullOrWhiteSpace(genericNameId))
        {
            if (await names.GetAsync(genericNameId, cancellationToken) is not { } existing)
            {
                return new SetGenericNameResult.Invalid("No such generic name.");
            }

            record = existing;
        }
        else
        {
            if (GenericNames.Problem(newName, current.Value.Details?.Brand) is { } problem)
            {
                return new SetGenericNameResult.Invalid(problem);
            }

            var id = GenericNames.IdOf(newName!);
            var existing = await names.GetAsync(id, cancellationToken);
            created = existing is null;
            record = existing ?? await names.GetOrCreateAsync(
                GenericName.Create(newName!, current.Value.Aisle?.Id ?? Aisles.Other.Id, clock.GetUtcNow(), "manual"), cancellationToken);
        }

        var updated = current.Value.WithManualGenericName(record, clock.GetUtcNow());
        if (!await products.TryReplaceAsync(current, updated, cancellationToken))
        {
            return new SetGenericNameResult.Conflict("The product changed meanwhile. Try again.");
        }

        LogSet(productId, record.Id, created, user.ObjectId);
        return new SetGenericNameResult.Saved(productId, updated.Generic!, created);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generic name for {ProductId} set by hand to {GenericNameId} (new name: {Created}) by {UserId}.")]
    private partial void LogSet(Guid productId, string genericNameId, bool created, Guid userId);
}
