using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.Adding;
using CupboardTracker.Domain.Stock;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// Store-label check-ins (#58) in the products container (id = "storeLabel:&lt;scanId&gt;", type "storeLabelCheckIn",
/// partitioned by /id like everything there): a handful of small documents, so no container of their own.
/// </summary>
public sealed class CosmosStoreLabelRepository(CosmosContainers containers) : IStoreLabelRepository
{
    private Container Products => containers.Products;

    private static string Id(Guid scanId) => $"storeLabel:{scanId:D}";

    public async Task<bool> CreateIfNotExistsAsync(StoreLabelCheckIn checkIn, CancellationToken cancellationToken)
    {
        var id = Id(checkIn.ScanId);
        using var stream = CosmosJson.ToStream(Write(checkIn));
        using var response = await Products.CreateItemStreamAsync(stream, new PartitionKey(id), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<Versioned<StoreLabelCheckIn>?> GetAsync(Guid scanId, CancellationToken cancellationToken)
    {
        var id = Id(scanId);
        using var response = await Products.ReadItemStreamAsync(id, new PartitionKey(id), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } d
            ? new Versioned<StoreLabelCheckIn>(Read(d), CosmosJson.Text(d["_etag"])!)
            : null;
    }

    public async Task<bool> TryReplaceAsync(Versioned<StoreLabelCheckIn> current, StoreLabelCheckIn updated, CancellationToken cancellationToken)
    {
        var id = Id(updated.ScanId);
        using var stream = CosmosJson.ToStream(Write(updated));
        using var response = await Products.ReplaceItemStreamAsync(
            stream, id, new PartitionKey(id), new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<IReadOnlyList<StoreLabelCheckIn>> ListUnresolvedAsync(CancellationToken cancellationToken)
    {
        var rows = await CosmosJson.QueryAsync(Products,
            new QueryDefinition("SELECT * FROM c WHERE c.type = 'storeLabelCheckIn' AND (NOT IS_DEFINED(c.resolvedAt) OR IS_NULL(c.resolvedAt))"),
            partitionKey: null, max: null, cancellationToken);
        return rows.Select(Read).ToList();
    }

    internal static JsonObject Write(StoreLabelCheckIn c) => new()
    {
        ["id"] = Id(c.ScanId),
        ["type"] = "storeLabelCheckIn",
        ["scanId"] = c.ScanId.ToString("D"),
        ["barcode"] = c.Barcode,
        ["deviceId"] = c.DeviceId,
        ["scannedAt"] = CosmosJson.Timestamp(c.ScannedAt),
        ["recordedAt"] = CosmosJson.Timestamp(c.RecordedAt),
        ["resolvedAt"] = CosmosJson.Timestamp(c.ResolvedAt),
        ["resolvedProductId"] = c.ResolvedProductId?.ToString("D"),
        ["resolvedBy"] = c.ResolvedBy?.ToString("D"),
        ["feedDismissedAt"] = CosmosJson.Timestamp(c.FeedDismissedAt),
    };

    internal static StoreLabelCheckIn Read(JsonObject d) => new(
        Guid.Parse(CosmosJson.Text(d["scanId"])!),
        CosmosJson.Text(d["barcode"]) ?? "",
        CosmosJson.Text(d["deviceId"]) ?? "",
        CosmosJson.ParseTimestamp(d["scannedAt"]) ?? DateTimeOffset.MinValue,
        CosmosJson.ParseTimestamp(d["recordedAt"]) ?? DateTimeOffset.MinValue,
        CosmosJson.ParseTimestamp(d["resolvedAt"]),
        Guid.TryParse(CosmosJson.Text(d["resolvedProductId"]), out var p) ? p : null,
        Guid.TryParse(CosmosJson.Text(d["resolvedBy"]), out var by) ? by : null,
        CosmosJson.ParseTimestamp(d["feedDismissedAt"]));
}
