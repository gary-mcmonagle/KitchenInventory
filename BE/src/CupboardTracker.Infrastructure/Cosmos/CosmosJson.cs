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

    public static MemoryStream ToStream(JsonObject document) => new(JsonSerializer.SerializeToUtf8Bytes(document));

    public static async Task<JsonObject?> ReadAsync(ResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await JsonNode.ParseAsync(response.Content, cancellationToken: cancellationToken) as JsonObject;
    }
}
