using System.Globalization;
using System.Text.Json.Nodes;
using CupboardTracker.Application.Scans;
using CupboardTracker.Domain;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>Devices: one document per deviceId (id = deviceId), partitioned by /deviceId (docs/data-model.md).</summary>
public sealed class CosmosDeviceRepository(CosmosContainers containers) : IDeviceRepository
{
    public async Task<Device?> GetAsync(string deviceId, CancellationToken cancellationToken)
    {
        var document = await ReadAsync(deviceId, cancellationToken);
        if (document is null || !RoleNames.TryParse(document["role"]?.GetValue<string>(), out var role))
        {
            return null;
        }

        return new Device(
            deviceId,
            role,
            ParseTimestamp(document["lastSeenAt"]) ?? DateTimeOffset.MinValue,
            ParseTimestamp(document["lastScanAt"]));
    }

    public async Task UpsertAsync(Device device, CancellationToken cancellationToken)
    {
        // Merge into the stored document so fields owned by later features are kept.
        var document = await ReadAsync(device.DeviceId, cancellationToken) ?? new JsonObject();
        document["id"] = device.DeviceId;
        document["deviceId"] = device.DeviceId;
        document["role"] = device.Role.ToWireName();
        document["lastSeenAt"] = CosmosJson.Timestamp(device.LastSeenAt);
        document["lastScanAt"] = device.LastScanAt is { } lastScan ? CosmosJson.Timestamp(lastScan) : null;
        foreach (var system in document.Select(p => p.Key).Where(k => k.StartsWith('_')).ToList())
        {
            document.Remove(system); // Cosmos system properties (_rid, _etag, ...) aren't written back
        }

        using var stream = CosmosJson.ToStream(document);
        using var response = await containers.Devices.UpsertItemStreamAsync(
            stream, new PartitionKey(device.DeviceId), cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonObject?> ReadAsync(string deviceId, CancellationToken cancellationToken)
    {
        using var response = await containers.Devices.ReadItemStreamAsync(
            deviceId, new PartitionKey(deviceId), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken);
    }

    private static DateTimeOffset? ParseTimestamp(JsonNode? node) =>
        node?.GetValue<string>() is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
}
