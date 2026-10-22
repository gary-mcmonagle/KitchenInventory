using System.Text.Json.Nodes;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Stock;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>The stock list's read: every in-stock unit (cross-partition over inventoryItems).</summary>
public sealed class CosmosStockQueries(CosmosContainers containers) : IStockQueries
{
    public async Task<IReadOnlyList<StockUnit>> GetInStockUnitsAsync(CancellationToken cancellationToken)
    {
        var rows = await CosmosJson.QueryAsync(
            containers.InventoryItems,
            new QueryDefinition("SELECT c.id, c.productId, c.barcode, c.checkedInAt, c.estimatedExpiryDate, c.expirySource, c.openedOn, c.frozenOn, c.defrostedOn, c.lastConfirmedAt, c.currentStorage FROM c WHERE c.status = 'inStock'"),
            partitionKey: null, max: null, cancellationToken);
        return rows.Select(ToUnit).ToList();
    }

    internal static StockUnit ToUnit(JsonObject row) => new(
        Guid.Parse(CosmosJson.Text(row["productId"])!),
        CosmosJson.Text(row["barcode"]),
        CosmosJson.ParseTimestamp(row["checkedInAt"]) ?? DateTimeOffset.MinValue,
        CosmosJson.ParseDate(row["estimatedExpiryDate"]),
        CosmosJson.Text(row["expirySource"]) switch
        {
            "opened" => ExpirySource.Opened,
            "frozen" => ExpirySource.Frozen,
            "defrosted" => ExpirySource.Defrosted,
            "manual" => ExpirySource.Manual,
            _ => row["estimatedExpiryDate"] is null ? null : ExpirySource.ShelfLife,
        },
        CosmosJson.ParseDate(row["openedOn"]),
        CosmosJson.ParseDate(row["frozenOn"]),
        CosmosJson.ParseDate(row["defrostedOn"]),
        Guid.TryParse(CosmosJson.Text(row["id"]), out var id) ? id : default,
        CosmosJson.ParseTimestamp(row["lastConfirmedAt"]),
        ProductDocument.ParseStorage(CosmosJson.Text(row["currentStorage"])));
}
