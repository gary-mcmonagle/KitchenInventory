using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>Documents go to and from Cosmos as raw JSON (System.Text.Json), so stored shapes are exactly as written.</summary>
internal static class CosmosJson
{
    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static JsonNode? Timestamp(DateTimeOffset? value) => value is { } v ? Timestamp(v) : null;

    public static DateTimeOffset? ParseTimestamp(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    public static DateOnly? ParseDate(JsonNode? node) =>
        DateOnly.TryParseExact(Text(node), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    public static MemoryStream ToStream(JsonObject document)
    {
        foreach (var system in document.Select(p => p.Key).Where(k => k.StartsWith('_')).ToList())
        {
            document.Remove(system); // Cosmos system properties (_rid, _etag, ...) are never written back
        }

        return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document));
    }

    public static async Task<JsonObject?> ReadAsync(ResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await JsonNode.ParseAsync(response.Content, cancellationToken: cancellationToken) as JsonObject;
    }

    public static async Task<List<JsonObject>> QueryAsync(
        Container container, QueryDefinition query, PartitionKey? partitionKey, int? max, CancellationToken cancellationToken)
    {
        var results = new List<JsonObject>();
        var options = new QueryRequestOptions { PartitionKey = partitionKey, MaxItemCount = max };
        using var iterator = container.GetItemQueryStreamIterator(query, requestOptions: options);
        while (iterator.HasMoreResults && (max is null || results.Count < max))
        {
            using var response = await iterator.ReadNextAsync(cancellationToken);
            response.EnsureSuccessStatusCode();
            var page = await JsonNode.ParseAsync(response.Content, cancellationToken: cancellationToken);
            results.AddRange(page?["Documents"]?.AsArray().OfType<JsonObject>() ?? []);
        }

        return max is { } m ? results.Take(m).ToList() : results;
    }
}
