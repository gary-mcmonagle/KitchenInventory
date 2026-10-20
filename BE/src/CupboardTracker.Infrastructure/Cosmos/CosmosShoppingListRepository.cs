using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.ShoppingList;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// The shoppingList container (#84): one household list in one partition (<c>listId: "household"</c>). Items
/// (<c>type: "item"</c>, id = the app's item id), tombstones (items with <c>removed: true</c>, which expire after a week)
/// and list memory (<c>type: "memory"</c>, id <c>memory:&lt;normalized name&gt;</c>).
/// </summary>
public sealed class CosmosShoppingListRepository(CosmosContainers containers) : IShoppingListRepository
{
    public const string ListId = "household";

    /// <summary>Long enough for any app to catch up; then Cosmos deletes it.</summary>
    public static readonly TimeSpan TombstoneTtl = TimeSpan.FromDays(7);

    private static readonly PartitionKey Partition = new(ListId);

    private Container Container => containers.ShoppingList;

    public async Task<Versioned<ShoppingListItem>?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await Container.ReadItemStreamAsync(id.ToString("D"), Partition, cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } d && CosmosJson.Text(d["type"]) == "item" ? Versioned(d) : null;
    }

    public async Task<IReadOnlyList<Versioned<ShoppingListItem>>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await CosmosJson.QueryAsync(Container, new QueryDefinition("SELECT * FROM c WHERE c.type = 'item'"), Partition, null, cancellationToken))
            .Select(Versioned)];

    public async Task<bool> TryCreateAsync(ShoppingListItem item, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(item));
        using var response = await Container.CreateItemStreamAsync(stream, Partition, cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> TryReplaceAsync(Versioned<ShoppingListItem> current, ShoppingListItem updated, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(updated));
        using var response = await Container.ReplaceItemStreamAsync(
            stream, updated.Id.ToString("D"), Partition, new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<ListMemory?> GetMemoryAsync(string normalizedName, CancellationToken cancellationToken)
    {
        using var response = await Container.ReadItemStreamAsync(MemoryId(normalizedName), Partition, cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } d ? ToMemory(d) : null;
    }

    public async Task<IReadOnlyList<ListMemory>> ListMemoryAsync(CancellationToken cancellationToken) =>
        [.. (await CosmosJson.QueryAsync(Container, new QueryDefinition("SELECT * FROM c WHERE c.type = 'memory'"), Partition, null, cancellationToken))
            .Select(ToMemory)];

    public async Task SaveMemoryAsync(ListMemory memory, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(new JsonObject
        {
            ["id"] = MemoryId(memory.NormalizedName),
            ["listId"] = ListId,
            ["type"] = "memory",
            ["normalizedName"] = memory.NormalizedName,
            ["name"] = memory.Name,
            ["aisle"] = memory.Aisle,
            ["timesAdded"] = memory.TimesAdded,
            ["lastAddedAt"] = CosmosJson.Timestamp(memory.LastAddedAt),
        });
        using var response = await Container.UpsertItemStreamAsync(stream, Partition, cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Cosmos ids can't contain / \ ? #, and names can.</summary>
    internal static string MemoryId(string normalizedName) =>
        "memory:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedName)))[..32].ToLowerInvariant();

    internal static JsonObject ToDocument(ShoppingListItem item)
    {
        var document = new JsonObject
        {
            ["id"] = item.Id.ToString("D"),
            ["listId"] = ListId,
            ["type"] = "item",
            ["name"] = item.Name,
            ["normalizedName"] = item.NormalizedName,
            ["note"] = item.Note,
            ["productId"] = item.ProductId?.ToString("D"),
            ["aisle"] = item.Aisle,
            ["aisleSource"] = item.AisleSource,
            ["source"] = item.Source,
            ["status"] = item.Status == ListItemStatus.Done ? "done" : "open",
            ["addedBy"] = item.AddedBy.ToString("D"),
            ["addedByName"] = item.AddedByName,
            ["addedAt"] = CosmosJson.Timestamp(item.AddedAt),
            ["doneBy"] = item.DoneBy?.ToString("D"),
            ["doneByName"] = item.DoneByName,
            ["doneAt"] = CosmosJson.Timestamp(item.DoneAt),
            ["updatedAt"] = CosmosJson.Timestamp(item.UpdatedAt),
            ["version"] = item.Version,
            ["changeId"] = item.ChangeId,
            ["removed"] = item.Removed,
            ["statusChangedAt"] = CosmosJson.Timestamp(item.StatusChangedAt),
        };
        if (item.Removed)
        {
            document["ttl"] = (int)TombstoneTtl.TotalSeconds;
        }

        return document;
    }

    internal static ShoppingListItem FromDocument(JsonObject d) => new(
        Guid.Parse(CosmosJson.Text(d["id"])!),
        CosmosJson.Text(d["name"]) ?? "",
        CosmosJson.Text(d["normalizedName"]) ?? "",
        CosmosJson.Text(d["note"]),
        Guid.TryParse(CosmosJson.Text(d["productId"]), out var productId) ? productId : null,
        CosmosJson.Text(d["aisle"]),
        CosmosJson.Text(d["aisleSource"]),
        CosmosJson.Text(d["source"]) ?? ListItemSources.Typed,
        CosmosJson.Text(d["status"]) == "done" ? ListItemStatus.Done : ListItemStatus.Open,
        Guid.TryParse(CosmosJson.Text(d["addedBy"]), out var addedBy) ? addedBy : Guid.Empty,
        CosmosJson.Text(d["addedByName"]) ?? "",
        CosmosJson.ParseTimestamp(d["addedAt"]) ?? DateTimeOffset.MinValue,
        Guid.TryParse(CosmosJson.Text(d["doneBy"]), out var doneBy) ? doneBy : null,
        CosmosJson.Text(d["doneByName"]),
        CosmosJson.ParseTimestamp(d["doneAt"]),
        CosmosJson.ParseTimestamp(d["updatedAt"]) ?? DateTimeOffset.MinValue,
        d["version"] is JsonValue v && v.TryGetValue<long>(out var version) ? version : 1,
        CosmosJson.Text(d["changeId"]) ?? "",
        d["removed"] is JsonValue r && r.TryGetValue<bool>(out var removed) && removed,
        CosmosJson.ParseTimestamp(d["statusChangedAt"]));

    private static Versioned<ShoppingListItem> Versioned(JsonObject d) => new(FromDocument(d), CosmosJson.Text(d["_etag"])!);

    private static ListMemory ToMemory(JsonObject d) => new(
        CosmosJson.Text(d["normalizedName"]) ?? "",
        CosmosJson.Text(d["name"]) ?? "",
        CosmosJson.Text(d["aisle"]),
        d["timesAdded"] is JsonValue t && t.TryGetValue<int>(out var times) ? times : 0,
        CosmosJson.ParseTimestamp(d["lastAddedAt"]) ?? DateTimeOffset.MinValue);
}
