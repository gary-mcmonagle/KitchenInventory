using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using CupboardTracker.Application.AiModels;

namespace CupboardTracker.Infrastructure.ModelTesting;

/// <summary>What a probe got back: the call as recorded (numbers and Foundry's own error), and the reply's text when it
/// worked. The text is for the check to inspect; it never leaves the check.</summary>
internal sealed record ProbeReply(AiCall Call, string? Content)
{
    public bool Ok => Call.Outcome == AiCall.Ok && Content is not null;
}

/// <summary>One plain chat call, no retries (#121): the first three checks ask a model small questions directly, so a refusal
/// is seen as it is rather than retried away.</summary>
internal sealed class FoundryProbe(HttpClient http, Uri endpoint, TokenCredential credential)
{
    private const string ApiVersion = "2024-10-21";
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    public async Task<ProbeReply> ChatAsync(
        string deployment, string system, string user, double? temperature, JsonObject? schema, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var json = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        if (schema is not null)
        {
            json["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = schema.DeepClone() };
        }

        if (temperature is { } sent)
        {
            json["temperature"] = sent;
        }

        var url = new Uri(endpoint, $"openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version={ApiVersion}");
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), limit.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await http.SendAsync(request, limit.Token);
            var text = await response.Content.ReadAsStringAsync(limit.Token);
            var recorded = Record(deployment, started, response.StatusCode, response.IsSuccessStatusCode, text);
            return new ProbeReply(recorded, response.IsSuccessStatusCode ? Content(text) : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeReply(Failure(deployment, started, AiCall.Timeout), null);
        }
        catch (HttpRequestException)
        {
            return new ProbeReply(Failure(deployment, started, AiCall.Network), null);
        }
        catch (JsonException)
        {
            return new ProbeReply(Failure(deployment, started, AiCall.Unreadable), null);
        }
    }

    private static AiCall Record(string deployment, long started, System.Net.HttpStatusCode status, bool success, string text)
    {
        FoundryCalls.Reply(deployment, started, status, success, text);
        return AiCallLog.Current!.Calls[^1];
    }

    private static AiCall Failure(string deployment, long started, string outcome)
    {
        FoundryCalls.Failure(deployment, started, outcome);
        return AiCallLog.Current!.Calls[^1];
    }

    private static string? Content(string responseBody) =>
        JsonNode.Parse(responseBody)?["choices"]?[0]?["message"]?["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
