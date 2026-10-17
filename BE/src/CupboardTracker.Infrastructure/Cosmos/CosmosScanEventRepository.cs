using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CupboardTracker.Application.Scans;
using CupboardTracker.Domain;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>Scan events: append-only, id = scan ID, partitioned by /deviceId (docs/data-model.md).</summary>
public sealed class CosmosScanEventRepository(CosmosContainers containers) : IScanEventRepository
{
    public async Task<bool> CreateIfNotExistsAsync(
        ScanEvent scanEvent, IReadOnlyDictionary<string, JsonElement> extraFields, CancellationToken cancellationToken)
    {
        var document = new JsonObject
        {
            ["id"] = scanEvent.ScanId.ToString("D"),
            ["deviceId"] = scanEvent.DeviceId,
            ["role"] = scanEvent.Role.ToWireName(),
            ["barcode"] = scanEvent.Barcode.Value,
            ["scannedAt"] = CosmosJson.Timestamp(scanEvent.ScannedAt),
            ["receivedAt"] = CosmosJson.Timestamp(scanEvent.ReceivedAt),
            ["clientSha"] = scanEvent.ClientSha,
        };
        foreach (var (name, value) in extraFields)
        {
            document[name] = JsonNode.Parse(value.GetRawText()); // stored as-is (e.g. source)
        }

        using var stream = CosmosJson.ToStream(document);
        using var response = await containers.ScanEvents.CreateItemStreamAsync(
            stream, new PartitionKey(scanEvent.DeviceId), cancellationToken: cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false; // already stored: a resend
        }

        response.EnsureSuccessStatusCode();
        return true;
    }
}
