using CupboardTracker.Application.Products;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.Adding;

/// <summary>
/// Finds the existing product a typed or recognised name means (#58): any known product, barcoded or loose, whose
/// display name is the same ignoring case and extra spaces. A photographed tin of bran flakes adds to the "Bran
/// Flakes" already in stock rather than creating a second product.
/// </summary>
public sealed class ProductMatcher(IProductRepository products)
{
    private Dictionary<string, Product>? byName;

    public async Task<Product?> FindAsync(string name, CancellationToken cancellationToken)
    {
        if (byName is null)
        {
            byName = [];
            // Prefer a loose product, then the most recently named, when two share a name.
            foreach (var p in (await products.ListKnownAsync(cancellationToken))
                         .OrderBy(p => p.IsLoose ? 0 : 1).ThenByDescending(p => p.NamedAt ?? p.FetchedAt))
            {
                byName.TryAdd(p.NameKey, p);
            }
        }

        return byName.GetValueOrDefault(Product.NameKeyOf(name));
    }
}
