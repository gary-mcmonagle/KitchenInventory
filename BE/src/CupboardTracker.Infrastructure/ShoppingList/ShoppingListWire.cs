using System.Globalization;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Infrastructure.ShoppingList;

/// <summary>
/// A shopping list item's JSON (#84, docs/api.md): the same shape comes back from the HTTP API and goes over SignalR in
/// <c>shoppingListItemChanged</c>. People are shown by name only, never their object IDs.
/// </summary>
public static class ShoppingListWire
{
    public static IReadOnlyDictionary<string, object?> Item(ShoppingListItem item) => new Dictionary<string, object?>
    {
        ["id"] = item.Id.ToString("D"),
        ["name"] = item.Name,
        ["note"] = item.Note,
        ["productId"] = item.ProductId?.ToString("D"),
        ["aisleId"] = item.Aisle,
        ["aisleName"] = item.Aisle is { } aisle ? Aisles.Of(aisle).Name : null,
        ["findingAisle"] = item.FindingAisle,
        ["source"] = item.Source,
        ["status"] = item.Status == ListItemStatus.Done ? "done" : "open",
        ["addedByName"] = item.AddedByName,
        ["addedAt"] = Timestamp(item.AddedAt),
        ["doneByName"] = item.DoneByName,
        ["doneAt"] = item.DoneAt is { } d ? Timestamp(d) : null,
        ["updatedAt"] = Timestamp(item.UpdatedAt),
        ["version"] = item.Version,
        ["removed"] = item.Removed,
    };

    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
