using System.Text.Json.Nodes;
using CupboardTracker.Application.Digest;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>The digest's cross-partition reads over inventoryItems. Timestamps are stored in one fixed ISO format
/// (CosmosJson.Timestamp), so string comparison is time order.</summary>
public sealed class CosmosDigestQueries(CosmosContainers containers) : IDigestQueries
{
    public Task<IReadOnlyList<DigestUnit>> GetInStockAsync(CancellationToken cancellationToken) =>
        QueryAsync(new QueryDefinition("SELECT c.productId, c.barcode, c.estimatedExpiryDate FROM c WHERE c.status = 'inStock'"), cancellationToken);

    public Task<IReadOnlyList<DigestUnit>> GetCheckedInAsync(DigestWindow window, CancellationToken cancellationToken) =>
        QueryAsync(InWindow("SELECT c.productId, c.barcode FROM c WHERE c.checkedInAt >= @start AND c.checkedInAt < @end", window),
            cancellationToken);

    public Task<IReadOnlyList<DigestUnit>> GetCheckedOutAsync(DigestWindow window, CancellationToken cancellationToken) =>
        QueryAsync(InWindow(
            "SELECT c.productId, c.barcode FROM c WHERE c.status = 'checkedOut' AND c.checkedOutAt >= @start AND c.checkedOutAt < @end",
            window), cancellationToken);

    private static QueryDefinition InWindow(string sql, DigestWindow window) =>
        new QueryDefinition(sql)
            .WithParameter("@start", CosmosJson.Timestamp(window.Start))
            .WithParameter("@end", CosmosJson.Timestamp(window.End));

    private async Task<IReadOnlyList<DigestUnit>> QueryAsync(QueryDefinition query, CancellationToken cancellationToken)
    {
        var rows = await CosmosJson.QueryAsync(containers.InventoryItems, query, partitionKey: null, max: null, cancellationToken);
        return rows.Select(ToUnit).ToList();
    }

    internal static DigestUnit ToUnit(JsonObject row) =>
        new(Guid.Parse(CosmosJson.Text(row["productId"])!), CosmosJson.Text(row["barcode"]),
            CosmosJson.ParseDate(row["estimatedExpiryDate"]));
}
