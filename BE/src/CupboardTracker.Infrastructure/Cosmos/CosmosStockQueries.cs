using System.Text.Json.Nodes;
using CupboardTracker.Application.Stock;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>The stock list's read: every in-stock unit (cross-partition over inventoryItems).</summary>
public sealed class CosmosStockQueries(CosmosContainers containers) : IStockQueries
{
    public async Task<IReadOnlyList<StockUnit>> GetInStockUnitsAsync(CancellationToken cancellationToken)
    {
        var rows = await CosmosJson.QueryAsync(
            containers.InventoryItems,
            new QueryDefinition("SELECT c.productId, c.barcode, c.checkedInAt FROM c WHERE c.status = 'inStock'"),
            partitionKey: null, max: null, cancellationToken);
        return rows.Select(ToUnit).ToList();
    }

    internal static StockUnit ToUnit(JsonObject row) => new(
        Guid.Parse(CosmosJson.Text(row["productId"])!),
        CosmosJson.Text(row["barcode"]),
        CosmosJson.ParseTimestamp(row["checkedInAt"]) ?? DateTimeOffset.MinValue);
}
