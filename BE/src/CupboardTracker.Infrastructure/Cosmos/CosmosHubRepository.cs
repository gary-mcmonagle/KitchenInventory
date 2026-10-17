using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.Hubs;
using CupboardTracker.Domain;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>Hubs in the devices container: <c>kind: "hub"</c>, id = deviceId = hubId (docs/data-model.md).</summary>
public sealed class CosmosHubRepository(CosmosContainers containers) : IHubRepository
{
    public const string Kind = "hub";

    private Container Devices => containers.Devices;

    public async Task<Versioned<Hub>?> GetAsync(string hubId, CancellationToken cancellationToken)
    {
        using var response = await Devices.ReadItemStreamAsync(hubId, new PartitionKey(hubId), cancellationToken: cancellationToken);
        var document = await CosmosJson.ReadAsync(response, cancellationToken);
        return document is not null && CosmosJson.Text(document["kind"]) == Kind ? Versioned(document) : null;
    }

    public async Task<IReadOnlyList<Versioned<Hub>>> ListAsync(CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.kind = @kind").WithParameter("@kind", Kind);
        var documents = await CosmosJson.QueryAsync(Devices, query, partitionKey: null, max: null, cancellationToken);
        return documents.Select(Versioned).ToList();
    }

    public async Task<bool> TryCreateAsync(Hub hub, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(Write(hub));
        using var response = await Devices.CreateItemStreamAsync(stream, new PartitionKey(hub.HubId), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> TryReplaceAsync(Versioned<Hub> current, Hub updated, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(Write(updated));
        using var response = await Devices.ReplaceItemStreamAsync(
            stream, updated.HubId, new PartitionKey(updated.HubId),
            new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    internal static JsonObject Write(Hub hub)
    {
        var heartbeat = hub.LastHeartbeat;
        var lastHeartbeat = new JsonObject
        {
            ["sentAt"] = CosmosJson.Timestamp(heartbeat.SentAt),
            ["clientSha"] = heartbeat.ClientSha,
            ["uptimeSeconds"] = heartbeat.UptimeSeconds,
            ["usbInputDevices"] = new JsonArray(heartbeat.UsbInputDevices.Select(n => (JsonNode?)n).ToArray()),
        };
        if (heartbeat.Scanners is { } scanners)
        {
            lastHeartbeat["scanners"] = new JsonArray(scanners
                .Select(s => (JsonNode?)new JsonObject { ["deviceId"] = s.DeviceId, ["present"] = s.Present })
                .ToArray());
        }

        return new JsonObject
        {
            ["id"] = hub.HubId,
            ["deviceId"] = hub.HubId,
            ["kind"] = Kind,
            ["lastSeenAt"] = CosmosJson.Timestamp(hub.LastSeenAt),
            ["offlineAlertSentAt"] = CosmosJson.Timestamp(hub.OfflineAlertSentAt),
            ["lastHeartbeat"] = lastHeartbeat,
        };
    }

    internal static Hub Read(JsonObject document)
    {
        var heartbeat = document["lastHeartbeat"] as JsonObject ?? [];
        var scanners = (heartbeat["scanners"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(s => new ScannerPresence(CosmosJson.Text(s["deviceId"]) ?? "", s["present"]?.GetValue<bool>() ?? false))
            .ToList();
        return new Hub(
            CosmosJson.Text(document["id"])!,
            CosmosJson.ParseTimestamp(document["lastSeenAt"]) ?? DateTimeOffset.MinValue,
            CosmosJson.ParseTimestamp(document["offlineAlertSentAt"]),
            new HeartbeatDetails(
                CosmosJson.ParseTimestamp(heartbeat["sentAt"]) ?? DateTimeOffset.MinValue,
                CosmosJson.Text(heartbeat["clientSha"]) ?? "",
                heartbeat["uptimeSeconds"]?.GetValue<long>() ?? 0,
                (heartbeat["usbInputDevices"] as JsonArray)?.Select(n => CosmosJson.Text(n) ?? "").ToList() ?? [],
                scanners));
    }

    private static Versioned<Hub> Versioned(JsonObject document) =>
        new(Read(document), CosmosJson.Text(document["_etag"])!);
}
