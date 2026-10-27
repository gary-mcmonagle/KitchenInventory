using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using CupboardTracker.Application.Adding;
using CupboardTracker.Application.AiModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Infrastructure.Photos;

/// <summary>From app settings: AI_SERVICES_ENDPOINT and PHOTO_MODEL_DEPLOYMENT (spike #41: gpt-4o, Standard, UK South).</summary>
public sealed record PhotoRecognitionOptions(Uri? Endpoint, string Deployment)
{
    public const string DefaultDeployment = "gpt-4o";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public int Attempts { get; init; } = 3;

    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public static PhotoRecognitionOptions FromConfiguration(IConfiguration config) => new(
        Uri.TryCreate(config["AI_SERVICES_ENDPOINT"], UriKind.Absolute, out var uri) ? uri : null,
        config["PHOTO_MODEL_DEPLOYMENT"] is { Length: > 0 } d ? d : DefaultDeployment);
}

/// <summary>
/// Photo recognition with gpt-4o on Azure AI Foundry (#58, spike #41): one call per photo, the spike's v2 prompt and a
/// strict JSON schema, authenticated with the app's managed identity (the account has local auth disabled). The
/// output is cleaned up here. The image is never logged: only latency, tokens and the number of items.
/// </summary>
public sealed partial class FoundryPhotoRecogniser(
    HttpClient http, PhotoRecognitionOptions options, TokenCredential credential, TimeProvider clock,
    ILogger<FoundryPhotoRecogniser> logger, IActiveAiModels? models = null) : IProductPhotoRecogniser
{
    private const string ApiVersion = "2024-10-21";
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    internal const string Instructions =
        "You identify the food and drink in a photo taken in a UK home kitchen, for a household stock tracker. "
        + "The most important thing is WHAT each item is, specifically enough to shop for it again: e.g. 'milk chocolate "
        + "fruit and nut bar', 'pitted green olives', 'cabernet sauvignon red wine', 'soured cream', 'leeks', 'baguette'. "
        + "List each distinct item you can clearly identify. Leave out anything you can only guess at from a blurry shape "
        + "or colour in the background. Count identical items together in quantity. "
        + "brand and size are optional extras: give them ONLY if you can read them on that item's own pack in this photo. "
        + "Never take a brand or size from a neighbouring item, and never fill one in from what's typical: use null instead. "
        + "Use real JSON null, never the text 'null' or 'unknown'. "
        + "kind: packaged (a branded or own-brand pack), loose (fruit, veg, or bakery bread in a plain bag), storeLabelled "
        + "(ONLY when you can see a label printed in the shop with a price or weight, e.g. deli or weighed items), or notFood. "
        + "readable: false if the photo is too blurry or dark to tell what anything is.";

    internal static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "name": "kitchen_photo",
          "strict": true,
          "schema": {
            "type": "object",
            "additionalProperties": false,
            "required": ["readable", "items"],
            "properties": {
              "readable": { "type": "boolean" },
              "items": {
                "type": "array",
                "items": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["name", "brand", "size", "kind", "quantity"],
                  "properties": {
                    "name": { "type": "string" },
                    "brand": { "type": ["string", "null"] },
                    "size": { "type": ["string", "null"] },
                    "kind": { "type": "string", "enum": ["packaged", "loose", "storeLabelled", "notFood"] },
                    "quantity": { "type": "integer" }
                  }
                }
              }
            }
          }
        }
        """)!.AsObject();

    public async Task<PhotoRecognitionResult> RecogniseAsync(ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken)
    {
        if (options.Endpoint is null)
        {
            return new PhotoRecognitionResult.Unavailable("not configured");
        }

        // The photos job's active model (#120), else the app setting's deployment.
        var model = models is null
            ? new ResolvedModel(options.Deployment, TemperatureMode.Usual)
            : await models.ForAsync(AiJob.Photos, options.Deployment, cancellationToken);
        var json = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = Instructions },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "What's in this photo?" },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = $"data:{MediaType(jpeg.Span)};base64," + Convert.ToBase64String(jpeg.Span),
                                ["detail"] = "high",
                            },
                        }),
                }),
            ["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = Schema.DeepClone() },
        };
        if (model.Temperature(0) is { } temperature)
        {
            json["temperature"] = temperature; // left out for a model that only takes its default (#120)
        }

        var body = json.ToJsonString();

        var url = new Uri(options.Endpoint, $"openai/deployments/{Uri.EscapeDataString(model.Deployment)}/chat/completions?api-version={ApiVersion}");
        var delay = options.FirstRetryDelay;
        var reason = "no attempts";
        for (var attempt = 1; attempt <= options.Attempts; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Timeout);
                var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), timeout.Token);
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                using var response = await http.SendAsync(request, timeout.Token);
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                var ms = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                FoundryCalls.Reply(model.Deployment, started, response.StatusCode, response.IsSuccessStatusCode, text);
                if (response.IsSuccessStatusCode)
                {
                    var parsed = Parse(text);
                    LogRecognised(ms, model.Deployment, parsed.Tokens, parsed.Recognition.Items.Count);
                    return new PhotoRecognitionResult.Recognised(parsed.Recognition);
                }

                reason = $"HTTP {(int)response.StatusCode}";
                if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                {
                    var (code, message) = FoundryErrors.Reason(text);
                    LogRefused(reason, model.Deployment, code, message); // a bug or bad configuration: not retried
                    return new PhotoRecognitionResult.Unavailable(reason);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                reason = "timeout";
                FoundryCalls.Failure(model.Deployment, started, AiCall.Timeout);
            }
            catch (HttpRequestException e)
            {
                reason = $"network error ({e.HttpRequestError})";
                FoundryCalls.Failure(model.Deployment, started, AiCall.Network);
            }
            catch (JsonException)
            {
                reason = "unreadable answer";
                FoundryCalls.Failure(model.Deployment, started, AiCall.Unreadable);
            }

            if (attempt < options.Attempts)
            {
                await Task.Delay(delay, clock, cancellationToken);
                delay *= 2;
            }
        }

        return new PhotoRecognitionResult.Unavailable($"{reason} (after {options.Attempts} attempts)");
    }

    /// <summary>Phones send JPEG; the model test suite's pack image (#121) is a PNG. Anything else is sent as JPEG.</summary>
    internal static string MediaType(ReadOnlySpan<byte> image) =>
        image.Length >= 4 && image[0] == 0x89 && image[1] == 0x50 && image[2] == 0x4E && image[3] == 0x47 ? "image/png" : "image/jpeg";

    /// <summary>The model's answer, cleaned up: "null"/"unknown"/empty → null, trimmed, capped lengths, quantity 1–20,
    /// not-food items dropped.</summary>
    internal static (PhotoRecognition Recognition, int Tokens) Parse(string responseBody)
    {
        var root = JsonNode.Parse(responseBody)!;
        var tokens = root["usage"]?["total_tokens"]?.GetValue<int>() ?? 0;
        var content = root["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? throw new JsonException("no content");
        var answer = JsonNode.Parse(content)!;
        var items = new List<RecognisedItem>();
        foreach (var node in answer["items"]?.AsArray() ?? [])
        {
            var kind = (string?)node?["kind"] switch
            {
                "packaged" => ItemKind.Packaged,
                "loose" => ItemKind.Loose,
                "storeLabelled" => ItemKind.StoreLabelled,
                _ => (ItemKind?)null, // notFood, or anything unexpected
            };
            var name = Clean(node?["name"], 100);
            if (kind is null || name is null)
            {
                continue;
            }

            var quantity = node?["quantity"] is JsonValue q && q.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 20) : 1;
            items.Add(new RecognisedItem(name, Clean(node?["brand"], 60), Clean(node?["size"], 60), kind.Value, quantity));
        }

        var readable = answer["readable"] is not JsonValue r || !r.TryGetValue<bool>(out var ok) || ok;
        return (new PhotoRecognition(readable, items), tokens);
    }

    private static string? Clean(JsonNode? node, int max)
    {
        var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        if (string.IsNullOrEmpty(text) || text.Equals("null", StringComparison.OrdinalIgnoreCase)
            || text.Equals("unknown", StringComparison.OrdinalIgnoreCase) || text.Equals("n/a", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return text.Length > max ? text[..max].TrimEnd() : text;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Photo recognised by {Deployment} in {Ms} ms ({Tokens} tokens): {Items} items.")]
    private partial void LogRecognised(int ms, string deployment, int tokens, int items);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "The photo model refused the request ({Reason}) on {Deployment}: {FoundryCode}: {FoundryMessage}")]
    private partial void LogRefused(string reason, string deployment, string foundryCode, string foundryMessage);
}
