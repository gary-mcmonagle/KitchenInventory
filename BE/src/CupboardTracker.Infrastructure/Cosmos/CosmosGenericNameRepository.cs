using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application.GenericNaming;
using CupboardTracker.Domain.Products;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// Generic name records (#90): documents of type <c>genericName</c> in the <b>products</b> container (partition /id),
/// with <c>id = "generic:&lt;normalized name&gt;"</c>. A create that conflicts means the name exists already, so two
/// products can never make two records with the same id. No new container is needed (docs/data-model.md).
/// </summary>
public sealed class CosmosGenericNameRepository(CosmosContainers containers) : IGenericNameRepository
{
    private const string Prefix = "generic:";

    public async Task<IReadOnlyList<GenericName>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await CosmosJson.QueryAsync(containers.Products, new QueryDefinition("SELECT * FROM c WHERE c.type = 'genericName'"),
            null, null, cancellationToken)).Select(FromDocument).OfType<GenericName>()];

    public async Task<GenericName?> GetAsync(string id, CancellationToken cancellationToken)
    {
        var key = Prefix + id;
        using var response = await containers.Products.ReadItemStreamAsync(key, new PartitionKey(key), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } document ? FromDocument(document) : null;
    }

    public async Task<GenericName> GetOrCreateAsync(GenericName name, CancellationToken cancellationToken)
    {
        var key = Prefix + name.Id;
        using var stream = CosmosJson.ToStream(ToDocument(name));
        using var response = await containers.Products.CreateItemStreamAsync(stream, new PartitionKey(key), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return await GetAsync(name.Id, cancellationToken) ?? name;
        }

        response.EnsureSuccessStatusCode();
        return name;
    }

    public async Task<GenericName?> SetAddWhenOutAsync(string id, bool enabled, Guid by, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var key = Prefix + id;
        var operations = new[]
        {
            PatchOperation.Set("/addWhenOut", enabled),
            PatchOperation.Set("/addWhenOutBy", by.ToString()),
            PatchOperation.Set("/addWhenOutAt", CosmosJson.Timestamp(at)),
        };
        using var response = await containers.Products.PatchItemStreamAsync(key, new PartitionKey(key), operations, cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } document ? FromDocument(document) : null;
    }

    internal static JsonObject ToDocument(GenericName name) => new()
    {
        ["id"] = Prefix + name.Id,
        ["type"] = "genericName",
        ["name"] = name.Name,
        ["aisle"] = name.AisleId,
        ["createdAt"] = CosmosJson.Timestamp(name.CreatedAt),
        ["createdBy"] = name.CreatedBy,
        ["addWhenOut"] = name.AddWhenOut,
        ["addWhenOutBy"] = name.AddWhenOutBy?.ToString(),
        ["addWhenOutAt"] = name.AddWhenOutAt is { } at ? CosmosJson.Timestamp(at) : null,
    };

    internal static GenericName? FromDocument(JsonObject d) =>
        CosmosJson.Text(d["id"]) is { } key && key.StartsWith(Prefix, StringComparison.Ordinal) && CosmosJson.Text(d["name"]) is { } name
            ? new GenericName(key[Prefix.Length..], name, CosmosJson.Text(d["aisle"]) ?? Aisles.Other.Id,
                CosmosJson.ParseTimestamp(d["createdAt"]) ?? DateTimeOffset.MinValue, CosmosJson.Text(d["createdBy"]) ?? GenericName.FoundrySource,
                d["addWhenOut"] is JsonValue v && v.TryGetValue<bool>(out var on) && on,
                Guid.TryParse(CosmosJson.Text(d["addWhenOutBy"]), out var by) ? by : null,
                CosmosJson.ParseTimestamp(d["addWhenOutAt"]))
            : null;
}
