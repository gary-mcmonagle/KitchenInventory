using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application.Push;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>pushSubscriptions (#77): one document per device, partitioned by /userId (the Entra oid), id = a hash of
/// the endpoint (docs/data-model.md).</summary>
public sealed class CosmosPushSubscriptionRepository(CosmosContainers containers) : IPushSubscriptionRepository
{
    public async Task<PushSubscription?> GetAsync(Guid userId, string id, CancellationToken cancellationToken)
    {
        using var response = await containers.PushSubscriptions.ReadItemStreamAsync(id, Key(userId), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } document ? FromDocument(document) : null;
    }

    public async Task UpsertAsync(PushSubscription subscription, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(subscription));
        using var response = await containers.PushSubscriptions.UpsertItemStreamAsync(stream, Key(subscription.UserId), cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> DeleteAsync(Guid userId, string id, CancellationToken cancellationToken)
    {
        using var response = await containers.PushSubscriptions.DeleteItemStreamAsync(id, Key(userId), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<IReadOnlyList<PushSubscription>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await CosmosJson.QueryAsync(containers.PushSubscriptions, new QueryDefinition("SELECT * FROM c"), null, null, cancellationToken))
            .Select(FromDocument).OfType<PushSubscription>()];

    public async Task MarkDeliveredAsync(Guid userId, string id, DateTimeOffset at, CancellationToken cancellationToken)
    {
        using var response = await containers.PushSubscriptions.PatchItemStreamAsync(
            id, Key(userId), [PatchOperation.Set("/lastSuccessAt", CosmosJson.Timestamp(at))], cancellationToken: cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound) // deleted meanwhile: nothing to record
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private static PartitionKey Key(Guid userId) => new(userId.ToString());

    internal static JsonObject ToDocument(PushSubscription s) => new()
    {
        ["id"] = s.Id,
        ["userId"] = s.UserId.ToString(),
        ["endpoint"] = s.Endpoint,
        ["keys"] = new JsonObject { ["p256dh"] = s.P256dh, ["auth"] = s.Auth },
        ["deviceLabel"] = s.DeviceLabel,
        ["createdAt"] = CosmosJson.Timestamp(s.CreatedAt),
        ["lastSeenAt"] = CosmosJson.Timestamp(s.LastSeenAt),
        ["lastSuccessAt"] = CosmosJson.Timestamp(s.LastSuccessAt),
        ["preferences"] = new JsonObject(s.Preferences.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),
    };

    internal static PushSubscription? FromDocument(JsonObject d)
    {
        if (!Guid.TryParse(CosmosJson.Text(d["userId"]), out var userId)
            || CosmosJson.Text(d["id"]) is not { } id
            || CosmosJson.Text(d["endpoint"]) is not { } endpoint
            || CosmosJson.Text(d["keys"]?["p256dh"]) is not { } p256dh
            || CosmosJson.Text(d["keys"]?["auth"]) is not { } auth)
        {
            return null;
        }

        var preferences = (d["preferences"] as JsonObject ?? [])
            .Where(p => p.Value is JsonValue v && v.TryGetValue<bool>(out _))
            .ToDictionary(p => p.Key, p => p.Value!.GetValue<bool>());
        var created = CosmosJson.ParseTimestamp(d["createdAt"]) ?? DateTimeOffset.MinValue;
        return new PushSubscription(id, userId, endpoint, p256dh, auth, CosmosJson.Text(d["deviceLabel"]) ?? "Browser",
            created, CosmosJson.ParseTimestamp(d["lastSeenAt"]) ?? created, CosmosJson.ParseTimestamp(d["lastSuccessAt"]), preferences);
    }
}

/// <summary>pushSeen (#77), partitioned by /eventKey: "seen" receipts (<c>id = eventKey:oid</c>) and push job claims
/// (<c>id = eventKey = job:&lt;jobId&gt;</c>). Every document carries a <c>ttl</c>, so Cosmos deletes it itself.</summary>
public sealed class CosmosPushSeenRepository(CosmosContainers containers) : IPushSeenRepository
{
    /// <summary>Receipts only matter for the minute or so before a push is sent.</summary>
    public static readonly TimeSpan ReceiptTtl = TimeSpan.FromDays(1);

    /// <summary>Job claims outlive the queue's message retention (7 days), so a late re-delivery still finds them.</summary>
    public static readonly TimeSpan JobClaimTtl = TimeSpan.FromDays(8);

    public async Task RecordAsync(Guid userId, IReadOnlyCollection<string> eventKeys, DateTimeOffset at, CancellationToken cancellationToken)
    {
        foreach (var key in eventKeys)
        {
            using var stream = CosmosJson.ToStream(Receipt(key, userId, at));
            using var response = await containers.PushSeen.UpsertItemStreamAsync(stream, new PartitionKey(key), cancellationToken: cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<IReadOnlySet<Guid>> UsersWhoSawAsync(string eventKey, CancellationToken cancellationToken)
    {
        var documents = await CosmosJson.QueryAsync(containers.PushSeen,
            new QueryDefinition("SELECT c.userId FROM c WHERE c.eventKey = @key AND IS_DEFINED(c.userId)").WithParameter("@key", eventKey),
            new PartitionKey(eventKey), null, cancellationToken);
        return documents.Select(d => Guid.TryParse(CosmosJson.Text(d["userId"]), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToHashSet();
    }

    public async Task<bool> TryClaimJobAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var key = $"job:{jobId}";
        using var stream = CosmosJson.ToStream(new JsonObject
        {
            ["id"] = key,
            ["eventKey"] = key,
            ["claimedAt"] = CosmosJson.Timestamp(at),
            ["ttl"] = (int)JobClaimTtl.TotalSeconds,
        });
        using var response = await containers.PushSeen.CreateItemStreamAsync(stream, new PartitionKey(key), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    internal static JsonObject Receipt(string eventKey, Guid userId, DateTimeOffset at) => new()
    {
        ["id"] = $"{eventKey}:{userId}",
        ["eventKey"] = eventKey,
        ["userId"] = userId.ToString(),
        ["seenAt"] = CosmosJson.Timestamp(at),
        ["ttl"] = (int)ReceiptTtl.TotalSeconds,
    };
}
