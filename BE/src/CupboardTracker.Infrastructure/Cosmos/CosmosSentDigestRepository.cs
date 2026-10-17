using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application.Digest;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>sentDigests: one document per digest day (id = the window's end date, e.g. "2026-10-18"), partitioned by
/// /id (docs/data-model.md).</summary>
public sealed class CosmosSentDigestRepository(CosmosContainers containers) : ISentDigestRepository
{
    public async Task<bool> TryClaimAsync(DigestWindow window, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var id = Id(window.Date);
        using var stream = CosmosJson.ToStream(new JsonObject
        {
            ["id"] = id,
            ["windowStart"] = CosmosJson.Timestamp(window.Start),
            ["windowEnd"] = CosmosJson.Timestamp(window.End),
            ["claimedAt"] = CosmosJson.Timestamp(at),
        });
        using var response = await containers.SentDigests.CreateItemStreamAsync(
            stream, new PartitionKey(id), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task ReleaseAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var id = Id(day);
        using var response = await containers.SentDigests.DeleteItemStreamAsync(
            id, new PartitionKey(id), cancellationToken: cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    internal static string Id(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
