using CupboardTracker.Application.Products;

namespace CupboardTracker.Application.Adding;

public sealed record LooseProductView(Guid ProductId, string DisplayName, int? ShelfLifeDays, DateTimeOffset? LastAddedAt);

/// <summary><c>GET /api/products/loose</c> (#58): the quick pick list, most recently added first, then A–Z.</summary>
public sealed class GetLooseProducts(IProductRepository products)
{
    public async Task<IReadOnlyList<LooseProductView>> ExecuteAsync(CancellationToken cancellationToken) =>
        [.. (await products.ListKnownAsync(cancellationToken))
            .Where(p => p.IsLoose)
            .OrderByDescending(p => p.LastAddedAt ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new LooseProductView(p.ProductId, p.DisplayName, p.ShelfLife?.Days, p.LastAddedAt))];
}
