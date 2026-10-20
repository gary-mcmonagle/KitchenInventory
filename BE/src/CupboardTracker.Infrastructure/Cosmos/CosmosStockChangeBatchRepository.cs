using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.Push;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// The open stock change batch (#80) in the pushSeen container: one document, id = eventKey = <c>batch:stock</c>, so
/// creating it twice conflicts and every change is an ETag-checked replace. Closing deletes it. It expires after a day
/// in case a job never runs (it's normally gone within five minutes).
/// </summary>
public sealed class CosmosStockChangeBatchRepository(CosmosContainers containers) : IStockChangeBatchRepository
{
    public const string Key = "batch:stock";

    public static readonly TimeSpan Ttl = TimeSpan.FromDays(1);

    private Container PushSeen => containers.PushSeen;

    public async Task<Versioned<StockChangeBatch>?> GetOpenAsync(CancellationToken cancellationToken)
    {
        using var response = await PushSeen.ReadItemStreamAsync(Key, new PartitionKey(Key), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } document
            ? new Versioned<StockChangeBatch>(FromDocument(document), CosmosJson.Text(document["_etag"])!)
            : null;
    }

    public async Task<bool> TryCreateAsync(StockChangeBatch batch, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(batch));
        using var response = await PushSeen.CreateItemStreamAsync(stream, new PartitionKey(Key), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> TryReplaceAsync(Versioned<StockChangeBatch> current, StockChangeBatch updated, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(updated));
        using var response = await PushSeen.ReplaceItemStreamAsync(
            stream, Key, new PartitionKey(Key), new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> TryCloseAsync(Versioned<StockChangeBatch> current, CancellationToken cancellationToken)
    {
        using var response = await PushSeen.DeleteItemStreamAsync(
            Key, new PartitionKey(Key), new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>A sent batch is kept as <c>batchSent:&lt;batchId&gt;</c> for <see cref="SentTtl"/> (#89).</summary>
    public static readonly TimeSpan SentTtl = TimeSpan.FromDays(3);

    public static string SentKey(Guid batchId) => $"batchSent:{batchId:D}";

    public async Task SaveSentAsync(StockChangeBatch batch, CancellationToken cancellationToken)
    {
        var key = SentKey(batch.BatchId);
        var document = ToDocument(batch, key, "stockChangeBatchSent", SentTtl);
        using var stream = CosmosJson.ToStream(document);
        using var response = await PushSeen.UpsertItemStreamAsync(stream, new PartitionKey(key), cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<StockChangeBatch?> GetSentAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var key = SentKey(batchId);
        using var response = await PushSeen.ReadItemStreamAsync(key, new PartitionKey(key), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } document ? FromDocument(document) : null;
    }

    internal static JsonObject ToDocument(StockChangeBatch batch) => ToDocument(batch, Key, "stockChangeBatch", Ttl);

    private static JsonObject ToDocument(StockChangeBatch batch, string key, string type, TimeSpan ttl) => new()
    {
        ["id"] = key,
        ["eventKey"] = key,
        ["type"] = type,
        ["batchId"] = batch.BatchId.ToString(),
        ["firstAt"] = CosmosJson.Timestamp(batch.FirstAt),
        ["lastAt"] = CosmosJson.Timestamp(batch.LastAt),
        ["changes"] = new JsonArray([.. batch.Changes.Select(c => (JsonNode)new JsonObject
        {
            ["eventKey"] = c.EventKey,
            ["kind"] = c.Kind == StockChangeKind.Added ? "added" : "removed",
            ["name"] = c.Name,
            ["at"] = CosmosJson.Timestamp(c.At),
            ["productId"] = c.ProductId?.ToString(),
            ["actorId"] = c.ActorId?.ToString(),
            ["actorName"] = c.ActorName,
        })]),
        ["ttl"] = (int)ttl.TotalSeconds,
    };

    internal static StockChangeBatch FromDocument(JsonObject document) => new(
        Guid.Parse(CosmosJson.Text(document["batchId"])!),
        CosmosJson.ParseTimestamp(document["firstAt"])!.Value,
        CosmosJson.ParseTimestamp(document["lastAt"])!.Value,
        [.. (document["changes"] as JsonArray ?? []).OfType<JsonObject>().Select(c => new StockChange(
            CosmosJson.Text(c["eventKey"])!,
            CosmosJson.Text(c["kind"]) == "removed" ? StockChangeKind.Removed : StockChangeKind.Added,
            CosmosJson.Text(c["name"]) ?? "",
            CosmosJson.ParseTimestamp(c["at"]) ?? DateTimeOffset.MinValue,
            Guid.TryParse(CosmosJson.Text(c["productId"]), out var productId) ? productId : null,
            Guid.TryParse(CosmosJson.Text(c["actorId"]), out var actorId) ? actorId : null,
            CosmosJson.Text(c["actorName"])))]);
}
