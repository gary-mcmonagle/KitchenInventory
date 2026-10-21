using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.Push;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// The open list batch (#86) in the pushSeen container, like the stock one: one document, id = eventKey =
/// <c>batch:list</c>, so creating it twice conflicts and every add is an ETag-checked replace. Closing deletes it. It
/// expires after a day in case a job never runs (it's normally gone within 15 minutes).
/// </summary>
public sealed class CosmosListPushBatchRepository(CosmosContainers containers) : IListPushBatchRepository
{
    public const string Key = "batch:list";

    public static readonly TimeSpan Ttl = TimeSpan.FromDays(1);

    private static readonly PartitionKey Partition = new(Key);

    private Container PushSeen => containers.PushSeen;

    public async Task<Versioned<ListPushBatch>?> GetOpenAsync(CancellationToken cancellationToken)
    {
        using var response = await PushSeen.ReadItemStreamAsync(Key, Partition, cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } document
            ? new Versioned<ListPushBatch>(FromDocument(document), CosmosJson.Text(document["_etag"])!)
            : null;
    }

    public async Task<bool> TryCreateAsync(ListPushBatch batch, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(batch));
        using var response = await PushSeen.CreateItemStreamAsync(stream, Partition, cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> TryReplaceAsync(Versioned<ListPushBatch> current, ListPushBatch updated, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(updated));
        using var response = await PushSeen.ReplaceItemStreamAsync(
            stream, Key, Partition, new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> TryCloseAsync(Versioned<ListPushBatch> current, CancellationToken cancellationToken)
    {
        using var response = await PushSeen.DeleteItemStreamAsync(
            Key, Partition, new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    internal static JsonObject ToDocument(ListPushBatch batch) => new()
    {
        ["id"] = Key,
        ["eventKey"] = Key,
        ["type"] = "listPushBatch",
        ["batchId"] = batch.BatchId.ToString(),
        ["firstAt"] = CosmosJson.Timestamp(batch.FirstAt),
        ["lastAt"] = CosmosJson.Timestamp(batch.LastAt),
        ["adds"] = batch.Adds,
        ["ttl"] = (int)Ttl.TotalSeconds,
    };

    internal static ListPushBatch FromDocument(JsonObject document) => new(
        Guid.Parse(CosmosJson.Text(document["batchId"])!),
        CosmosJson.ParseTimestamp(document["firstAt"])!.Value,
        CosmosJson.ParseTimestamp(document["lastAt"])!.Value,
        document["adds"]?.GetValue<int>() ?? 0);
}

/// <summary>
/// Each person's list push state (#86) in the shoppingList container, beside the list it's about: <c>type: "viewer"</c>,
/// id <c>viewer:&lt;oid&gt;</c>, in the household partition. Kept (no ttl); list queries filter on <c>type</c>, so
/// they never see it.
/// </summary>
public sealed class CosmosListViewerRepository(CosmosContainers containers) : IListViewerRepository
{
    private static readonly PartitionKey Partition = new(CosmosShoppingListRepository.ListId);

    private Container Container => containers.ShoppingList;

    public static string Id(Guid personId) => $"viewer:{personId:D}";

    public async Task<Versioned<ListViewer>?> GetAsync(Guid personId, CancellationToken cancellationToken)
    {
        using var response = await Container.ReadItemStreamAsync(Id(personId), Partition, cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } document
            ? new Versioned<ListViewer>(FromDocument(document), CosmosJson.Text(document["_etag"])!)
            : null;
    }

    public async Task<bool> TryCreateAsync(ListViewer viewer, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(viewer));
        using var response = await Container.CreateItemStreamAsync(stream, Partition, cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> TryReplaceAsync(Versioned<ListViewer> current, ListViewer updated, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(updated));
        using var response = await Container.ReplaceItemStreamAsync(
            stream, Id(updated.PersonId), Partition, new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    internal static JsonObject ToDocument(ListViewer viewer) => new()
    {
        ["id"] = Id(viewer.PersonId),
        ["listId"] = CosmosShoppingListRepository.ListId,
        ["type"] = "viewer",
        ["userId"] = viewer.PersonId.ToString(),
        ["listLastViewedAt"] = viewer.LastViewedAt is { } v ? CosmosJson.Timestamp(v) : null,
        ["lastListPushAt"] = viewer.LastPushAt is { } p ? CosmosJson.Timestamp(p) : null,
        ["listFollowUpAt"] = viewer.FollowUpAt is { } f ? CosmosJson.Timestamp(f) : null,
    };

    internal static ListViewer FromDocument(JsonObject document) => new(
        Guid.Parse(CosmosJson.Text(document["userId"])!),
        CosmosJson.ParseTimestamp(document["listLastViewedAt"]),
        CosmosJson.ParseTimestamp(document["lastListPushAt"]),
        CosmosJson.ParseTimestamp(document["listFollowUpAt"]));
}
