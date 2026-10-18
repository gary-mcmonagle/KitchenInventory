using System.Globalization;
using System.Text.Json.Nodes;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>The scan feed's catch-up reads (#54), all cross-partition and small. Timestamps are stored in one fixed
/// ISO format, so string comparison is time order.</summary>
public sealed class CosmosScanFeedQueries(CosmosContainers containers) : IScanFeedQueries
{
    public async Task<IReadOnlyList<ScanEvent>> GetScansSinceAsync(DateTimeOffset since, int max, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.receivedAt >= @since")
            .WithParameter("@since", CosmosJson.Timestamp(since));
        var rows = await CosmosJson.QueryAsync(containers.ScanEvents, query, partitionKey: null, max: null, cancellationToken);
        return rows.Select(ToScanEvent).OfType<ScanEvent>().OrderBy(s => s.ReceivedAt).Take(max).ToList();
    }

    public async Task<IReadOnlyList<Unit>> GetUnitsForScansAsync(IReadOnlyCollection<Guid> scanIds, CancellationToken cancellationToken)
    {
        if (scanIds.Count == 0)
        {
            return [];
        }

        var query = new QueryDefinition("SELECT * FROM c WHERE ARRAY_CONTAINS(@ids, c.id) OR ARRAY_CONTAINS(@ids, c.checkOutScanId)")
            .WithParameter("@ids", scanIds.Select(id => id.ToString("D")).ToArray());
        var rows = await CosmosJson.QueryAsync(containers.InventoryItems, query, partitionKey: null, max: null, cancellationToken);
        return rows.Select(CosmosUnitRepository.ToUnit).ToList();
    }

    public async Task<IReadOnlyList<UnmatchedCheckOut>> GetUnmatchedAsync(IReadOnlyCollection<Guid> scanIds, CancellationToken cancellationToken)
    {
        if (scanIds.Count == 0)
        {
            return [];
        }

        var query = new QueryDefinition("SELECT * FROM c WHERE ARRAY_CONTAINS(@ids, c.id)")
            .WithParameter("@ids", scanIds.Select(id => id.ToString("D")).ToArray());
        var rows = await CosmosJson.QueryAsync(containers.UnmatchedCheckOuts, query, partitionKey: null, max: null, cancellationToken);
        return rows.Select(d => new UnmatchedCheckOut(
            Guid.Parse(CosmosJson.Text(d["id"])!),
            CosmosJson.Text(d["barcode"]) ?? "",
            Guid.TryParse(CosmosJson.Text(d["productId"]), out var productId) ? productId : null,
            CosmosJson.Text(d["deviceId"]) ?? "",
            CosmosJson.ParseTimestamp(d["scannedAt"]) ?? DateTimeOffset.MinValue,
            CosmosJson.ParseTimestamp(d["recordedAt"]) ?? DateTimeOffset.MinValue)).ToList();
    }

    public async Task<IReadOnlyList<Product>> GetUnnamedProductsAsync(CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.type = 'product' AND (c.status = 'noMatch' OR c.status = 'lookupFailed')");
        var rows = await CosmosJson.QueryAsync(containers.Products, query, partitionKey: null, max: null, cancellationToken);
        return rows.Select(ProductDocument.ToProduct).ToList();
    }

    internal static ScanEvent? ToScanEvent(JsonObject d)
    {
        if (!Guid.TryParse(CosmosJson.Text(d["id"]), out var scanId)
            || !Barcode.TryCreate(CosmosJson.Text(d["barcode"]), out var barcode, out _)
            || CosmosJson.Text(d["deviceId"]) is not { } deviceId
            || !RoleNames.TryParse(CosmosJson.Text(d["role"]), out var role)
            || CosmosJson.ParseTimestamp(d["scannedAt"]) is not { } scannedAt)
        {
            return null;
        }

        return new ScanEvent(scanId, barcode!, deviceId, role, scannedAt, CosmosJson.ParseTimestamp(d["receivedAt"]) ?? scannedAt,
            CosmosJson.Text(d["clientSha"]) ?? "");
    }
}
