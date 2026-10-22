using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Runs a query to the end (or <paramref name="max"/> items) and records it as one call (#115): its RUs over every
    /// page, items, pages, duration, partition scope and status, named <c>{container}.{calling method}</c> (e.g.
    /// <c>inventoryItems.inStock</c> from <c>GetInStockAsync</c>). Asks for index metrics while they're switched on.
    /// </summary>
    public static async Task<List<JsonObject>> QueryAsync(
        Container container, QueryDefinition query, PartitionKey? partitionKey, int? max, CancellationToken cancellationToken,
        [CallerMemberName] string caller = "")
    {
        var handler = HandlerOf(container);
        var options = new QueryRequestOptions
        {
            PartitionKey = partitionKey,
            MaxItemCount = max,
            PopulateIndexMetrics = handler?.IndexMetrics == true,
        };
        using var _ = CosmosMetricsHandler.QueryScope();
        using var iterator = container.GetItemQueryStreamIterator(query, requestOptions: options);
        return await ReadAllAsync(
            iterator, OperationName(container.Id, caller), container.Id, partitionKey is null, max, handler?.Recorder, handler, cancellationToken);
    }

    /// <summary>Every page of a query (or up to <paramref name="max"/> items), recorded as one call however it ends.</summary>
    internal static async Task<List<JsonObject>> ReadAllAsync(
        FeedIterator iterator, string operation, string container, bool crossPartition, int? max, CosmosRecorder? recorder,
        CosmosMetricsHandler? handler, CancellationToken cancellationToken)
    {
        var results = new List<JsonObject>();
        var started = Stopwatch.GetTimestamp();
        int status = 0, pages = 0;
        double charge = 0;
        try
        {
            while (iterator.HasMoreResults && (max is null || results.Count < max))
            {
                using var response = await iterator.ReadNextAsync(cancellationToken);
                pages++;
                status = (int)response.StatusCode;
                charge += response.Headers.RequestCharge;
                if (pages == 1)
                {
                    handler?.IndexMetricsSeen(operation, response.IndexMetrics);
                }

                response.EnsureSuccessStatusCode();
                var page = await JsonNode.ParseAsync(response.Content, cancellationToken: cancellationToken);
                results.AddRange(page?["Documents"]?.AsArray().OfType<JsonObject>() ?? []);
            }
        }
        finally
        {
            var items = max is { } cap ? Math.Min(results.Count, cap) : results.Count;
            recorder?.Record(new CosmosCall(operation, container, crossPartition, status, charge, items, pages, Stopwatch.GetElapsedTime(started)));
        }

        return max is { } m ? results.Take(m).ToList() : results;
    }

    /// <summary>The measuring handler on the container's client, if any (none in tests that build their own client).</summary>
    internal static CosmosMetricsHandler? HandlerOf(Container container)
    {
        try
        {
            return container.Database.Client.ClientOptions.CustomHandlers.OfType<CosmosMetricsHandler>().FirstOrDefault();
        }
        catch (NotImplementedException)
        {
            return null; // a fake container
        }
    }

    /// <summary><c>inventoryItems</c> + <c>GetInStockWithoutExpiryAsync</c> → <c>inventoryItems.inStockWithoutExpiry</c>.</summary>
    internal static string OperationName(string container, string caller)
    {
        var name = caller.EndsWith("Async", StringComparison.Ordinal) ? caller[..^5] : caller;
        if (name.StartsWith("Get", StringComparison.Ordinal) && name.Length > 3 && char.IsUpper(name[3]))
        {
            name = name[3..];
        }

        return name.Length == 0 ? $"{container}.query" : $"{container}.{char.ToLowerInvariant(name[0])}{name[1..]}";
    }
}
