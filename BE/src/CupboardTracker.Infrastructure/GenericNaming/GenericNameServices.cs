using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using CupboardTracker.Application.AiModels;
using CupboardTracker.Application.GenericNaming;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Domain.Products;
using CupboardTracker.Infrastructure.Jev;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Infrastructure.GenericNaming;

/// <summary>
/// Matches a product to an existing generic name with a Jev <c>choice</c> (#90): the options plus "None of these",
/// keyed n0…nK so names never have to be valid keys. The wording is the one calibrated on 77 real products before
/// building (any brand or size of the same thing matches; a different variety doesn't).
/// </summary>
public sealed class JevGenericNameMatcher(JevClient client, JevOptions jevOptions) : IGenericNameMatcher
{
    internal const string Instructions =
        "Which of these shopping-list names is the same kind of product as this one, at the level a shopper buys it: "
        + "any brand or pack size of the same thing matches (any semi-skimmed milk is 'Semi-skimmed milk'), but a "
        + "different variety or type doesn't (caster sugar is not 'Icing sugar', smoked paprika is not 'Paprika'). "
        + "If none of them is the same kind of product, choose none.";

    internal const string NoneKey = "none";

    public async Task<GenericMatchResult> MatchAsync(
        ProductDescription product, IReadOnlyList<GenericName> options, CancellationToken cancellationToken)
    {
        if (!jevOptions.IsConfigured)
        {
            return new GenericMatchResult.NotConfigured();
        }

        if (options.Count == 0)
        {
            return new GenericMatchResult.NoMatch();
        }

        return await client.AskAsync(JevShelfLifeEstimator.State(product), Question(options), cancellationToken) switch
        {
            JevResponse.Answered { Answers: var answers } => Read(answers, options),
            JevResponse.Unavailable { Reason: var reason } => new GenericMatchResult.Unavailable(reason),
            JevResponse.Refused { Reason: var reason } => new GenericMatchResult.Failed(reason),
            var other => throw new InvalidOperationException($"Unexpected Jev response {other}."),
        };
    }

    internal static JsonObject Question(IReadOnlyList<GenericName> names)
    {
        var criteria = new JsonObject();
        for (var i = 0; i < names.Count; i++)
        {
            criteria[$"n{i}"] = names[i].Name;
        }

        criteria[NoneKey] = "None of these: a different kind of product";
        return new JsonObject { ["name"] = new JsonObject { ["type"] = "choice", ["instructions"] = Instructions, ["criteria"] = criteria } };
    }

    internal static GenericMatchResult Read(JsonObject answers, IReadOnlyList<GenericName> names)
    {
        if (answers["name"] is not JsonObject answer || answer["choice"] is not JsonValue c || !c.TryGetValue<string>(out var choice))
        {
            return new GenericMatchResult.Failed("no choice in Jev's answer");
        }

        var confidence = answer["confidence"] is JsonValue v && v.TryGetValue<double>(out var conf) ? conf : 0;
        if (choice == NoneKey)
        {
            return new GenericMatchResult.NoMatch();
        }

        return choice.StartsWith('n') && int.TryParse(choice.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index < names.Count
            ? new GenericMatchResult.Matched(names[index], confidence)
            : new GenericMatchResult.Failed($"unknown choice {choice}");
    }
}

/// <summary>From app settings (#90): AI_SERVICES_ENDPOINT, GENERIC_NAME_MODEL_DEPLOYMENT (default gpt-4o) and
/// GENERIC_NAME_FOUNDRY_MIN_INTERVAL_SECONDS (default 6: the deployment allows 10 requests per 10 s and about 10k
/// tokens a minute, shared with recipes and photo identification, so this takes at most ~1 in 6).</summary>
public sealed record GenericNameWriterOptions(Uri? Endpoint, string Deployment, TimeSpan MinInterval)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public int Attempts { get; init; } = 3;

    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public static GenericNameWriterOptions FromConfiguration(IConfiguration config) => new(
        Uri.TryCreate(config["AI_SERVICES_ENDPOINT"], UriKind.Absolute, out var uri) ? uri : null,
        config["GENERIC_NAME_MODEL_DEPLOYMENT"] is { Length: > 0 } d ? d : "gpt-4o",
        TimeSpan.FromSeconds(double.TryParse(config["GENERIC_NAME_FOUNDRY_MIN_INTERVAL_SECONDS"], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0 ? s : 6));
}

/// <summary>
/// Spaces Foundry calls for generic names (#90) at least <see cref="GenericNameWriterOptions.MinInterval"/> apart,
/// across the app instance, so a fill never starves recipes or photo identification.
/// </summary>
public sealed class FoundryThrottle(GenericNameWriterOptions options, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset next = DateTimeOffset.MinValue;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var wait = next - clock.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, clock, cancellationToken);
            }

            next = clock.GetUtcNow() + options.MinInterval;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}

/// <summary>
/// Writes a new level A generic name with gpt-4o on Foundry (#90, spike #82's prompt and rules), strict JSON schema
/// <c>{ genericName }</c>, the app's managed identity, through <see cref="FoundryThrottle"/>. The aisle's existing
/// names go in as examples of the style. The model doesn't decide reuse: the caller normalises its answer. Logs hold
/// latency, tokens and the deployment only.
/// </summary>
public sealed partial class FoundryGenericNameWriter(
    HttpClient http, GenericNameWriterOptions options, FoundryThrottle throttle, TokenCredential credential, TimeProvider clock,
    ILogger<FoundryGenericNameWriter> logger, IActiveAiModels? models = null) : IGenericNameWriter
{
    private const string ApiVersion = "2024-10-21";
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    /// <summary>Spike #82's level A instructions (docs/spikes/82-generic-names.py, LEVELS["A"]).</summary>
    internal const string Instructions =
        "You give a product in a UK household's food cupboard a short generic name for their shopping list: what you'd say "
        + "to someone going to the shop, so the same kind of thing always gets the same name whatever the brand. Rules: "
        + "English, UK spelling, even if the product name is in another language. Sentence case (first word capitalised, "
        + "the rest lower case except proper nouns such as Dijon, Parmigiano Reggiano, Weetabix). No brand, shop, range "
        + "(Deluxe, Taste of, Finest), size, weight, count, pack format (bag, tin, jar, pot, multipack) or marketing words "
        + "(classic, original, authentic, rich, British). Plural for things you count (Apples, Eggs, Carrots, Udon noodles); "
        + "singular for things you don't (Milk, Rice, Flour). Keep a brand only when it IS what people call the product and "
        + "there's no everyday generic word for it (e.g. Weetabix, Marmite). "
        + "Level: SHOPPER. Keep the descriptors that change what you'd pick up in the shop: the variety or type (semi-"
        + "skimmed, mature, wholewheat, smoked, caster, Greek, decaf, ground vs beans, chopped vs plum tomatoes, beef vs "
        + "vegetable stock), and what the dish is for ready-made food (Tikka masala paste, Macaroni cheese). Drop flavour "
        + "and variety detail only when any of that kind would do. Examples: 'Kellogg's Bran Flakes 500 g' -> 'Bran flakes'; "
        + "'Cathedral City Mature Cheddar 350g' -> 'Mature cheddar'; 'Tesco Semi Skimmed Milk 4 pints' -> 'Semi-skimmed "
        + "milk'; 'Heinz Baked Beanz' -> 'Baked beans'; 'Batts Smoked Paprika 40g' -> 'Smoked paprika'. "
        + "Names already used in this aisle are listed as examples of the style; none of them is the same kind of product.";

    internal static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "name": "generic_name",
          "strict": true,
          "schema": {
            "type": "object",
            "additionalProperties": false,
            "required": ["genericName"],
            "properties": { "genericName": { "type": "string" } }
          }
        }
        """)!.AsObject();

    public async Task<GenericWriteResult> WriteAsync(
        ProductDescription product, string aisleName, IReadOnlyList<string> examples, CancellationToken cancellationToken)
    {
        if (options.Endpoint is null)
        {
            return new GenericWriteResult.NotConfigured();
        }

        // The generic names job's active model (#120), else the app setting's deployment.
        var model = models is null
            ? new ResolvedModel(options.Deployment, TemperatureMode.Usual)
            : await models.ForAsync(AiJob.GenericNames, options.Deployment, cancellationToken);
        var json = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = Instructions },
                new JsonObject { ["role"] = "user", ["content"] = Prompt(product, aisleName, examples) }),
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
            await throttle.WaitAsync(cancellationToken);
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
                if (response.IsSuccessStatusCode)
                {
                    var (name, tokens) = Parse(text);
                    LogWritten((int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, model.Deployment, tokens);
                    return new GenericWriteResult.Written(name);
                }

                reason = $"HTTP {(int)response.StatusCode}";
                if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                {
                    var (code, message) = FoundryErrors.Reason(text);
                    LogRefused(reason, model.Deployment, code, message); // a bug or bad configuration: not retried
                    return new GenericWriteResult.Failed(reason);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                reason = "timeout";
            }
            catch (HttpRequestException e)
            {
                reason = $"network error ({e.HttpRequestError})";
            }
            catch (JsonException)
            {
                reason = "unreadable answer";
            }

            if (attempt < options.Attempts)
            {
                await Task.Delay(delay, clock, cancellationToken);
                delay *= 2;
            }
        }

        return new GenericWriteResult.Unavailable($"{reason} (after {options.Attempts} attempts)");
    }

    internal static string Prompt(ProductDescription product, string aisleName, IReadOnlyList<string> examples)
    {
        var text = new StringBuilder();
        text.Append("Product name: ").AppendLine(product.Name);
        if (product.Brand is { } brand)
        {
            text.Append("Brand: ").AppendLine(brand);
        }

        if (product.Size is { } size)
        {
            text.Append("Size: ").AppendLine(size);
        }

        var tags = product.CategoryTags.Where(t => t.StartsWith("en:", StringComparison.Ordinal)).Select(t => t[3..].Replace('-', ' ')).TakeLast(4).ToList();
        if (tags.Count > 0)
        {
            text.Append("Categories: ").AppendLine(string.Join(", ", tags));
        }

        text.Append("Aisle: ").AppendLine(aisleName);
        if (examples.Count > 0)
        {
            text.Append("Names already used in this aisle: ").AppendLine(string.Join(", ", examples));
        }

        return text.ToString();
    }

    internal static (string Name, int Tokens) Parse(string responseBody)
    {
        var root = JsonNode.Parse(responseBody)!;
        var content = root["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? throw new JsonException("no content");
        var name = JsonNode.Parse(content)?["genericName"]?.GetValue<string>()?.Trim();
        return (string.IsNullOrEmpty(name) ? throw new JsonException("no name") : name, root["usage"]?["total_tokens"]?.GetValue<int>() ?? 0);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generic name written by {Deployment} in {Ms} ms ({Tokens} tokens).")]
    private partial void LogWritten(int ms, string deployment, int tokens);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "The generic name model refused the request ({Reason}) on {Deployment}: {FoundryCode}: {FoundryMessage}")]
    private partial void LogRefused(string reason, string deployment, string foundryCode, string foundryMessage);
}
