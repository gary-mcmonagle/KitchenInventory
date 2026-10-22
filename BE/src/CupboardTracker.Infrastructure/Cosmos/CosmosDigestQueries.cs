using System.Text.Json.Nodes;
using CupboardTracker.Application.Digest;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>The digest's cross-partition reads over inventoryItems. Timestamps are stored in one fixed ISO format
/// (CosmosJson.Timestamp), so string comparison is time order.</summary>
public sealed class CosmosDigestQueries(CosmosContainers containers) : IDigestQueries
{
    public Task<IReadOnlyList<DigestUnit>> GetInStockAsync(CancellationToken cancellationToken) =>
        QueryAsync(new QueryDefinition("SELECT c.productId, c.barcode, c.estimatedExpiryDate, c.expirySource, c.openedOn, c.defrostedOn FROM c WHERE c.status = 'inStock'"), cancellationToken,
            "DigestInStock");

    public Task<IReadOnlyList<DigestUnit>> GetCheckedInAsync(DigestWindow window, CancellationToken cancellationToken) =>
        QueryAsync(InWindow("SELECT c.productId, c.barcode FROM c WHERE c.checkedInAt >= @start AND c.checkedInAt < @end", window),
            cancellationToken, "DigestCheckedIn");

    public Task<IReadOnlyList<DigestUnit>> GetCheckedOutAsync(DigestWindow window, CancellationToken cancellationToken) =>
        QueryAsync(InWindow(
            "SELECT c.productId, c.barcode FROM c WHERE c.status = 'checkedOut' AND c.checkedOutAt >= @start AND c.checkedOutAt < @end",
            window), cancellationToken, "DigestCheckedOut");

    private static QueryDefinition InWindow(string sql, DigestWindow window) =>
        new QueryDefinition(sql)
            .WithParameter("@start", CosmosJson.Timestamp(window.Start))
            .WithParameter("@end", CosmosJson.Timestamp(window.End));

    private async Task<IReadOnlyList<DigestUnit>> QueryAsync(
        QueryDefinition query, CancellationToken cancellationToken, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var rows = await CosmosJson.QueryAsync(containers.InventoryItems, query, partitionKey: null, max: null, cancellationToken, caller);
        return rows.Select(ToUnit).ToList();
    }

    internal static DigestUnit ToUnit(JsonObject row) =>
        new(Guid.Parse(CosmosJson.Text(row["productId"])!), CosmosJson.Text(row["barcode"]),
            CosmosJson.ParseDate(row["estimatedExpiryDate"]), StateNote(row));

    /// <summary>"opened 12 Oct" or "defrosted 12 Oct" when the estimate is from that (#65).</summary>
    internal static string? StateNote(JsonObject row) => CosmosJson.Text(row["expirySource"]) switch
    {
        "opened" when CosmosJson.ParseDate(row["openedOn"]) is { } on => $"opened {on.ToString("d MMM", Uk)}",
        "defrosted" when CosmosJson.ParseDate(row["defrostedOn"]) is { } on => $"defrosted {on.ToString("d MMM", Uk)}",
        _ => null,
    };

    private static readonly System.Globalization.CultureInfo Uk = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
}
