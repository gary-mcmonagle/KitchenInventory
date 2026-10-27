using System.Net;
using System.Text.Json.Nodes;
using Azure.Core;
using CupboardTracker.Application.AiModels;
using CupboardTracker.Application.GenericNaming;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Infrastructure;
using CupboardTracker.Infrastructure.Cosmos;
using CupboardTracker.Infrastructure.GenericNaming;
using CupboardTracker.Infrastructure.Photos;
using CupboardTracker.Infrastructure.Recipes;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Infrastructure.Tests;

/// <summary>The model each Foundry caller uses comes from the settings (#120): its deployment, whether `temperature` is
/// sent, and why a refusal happened.</summary>
public sealed class AiModelCallerTests
{
    private const string Refusal =
        """{"error":{"code":"unsupported_value","message":"Unsupported value: 'temperature' does not support 0.7 with this model. Only the default (1) value is supported.","param":"temperature"}}""";

    private static readonly Uri Endpoint = new("https://ais.example.test/");

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token-not-real", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Logs<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>A fixed answer for every job, remembering what it was asked.</summary>
    private sealed class FixedModels(ResolvedModel model) : IActiveAiModels
    {
        public List<(AiJob Job, string Fallback)> Asked { get; } = [];

        public Task<ResolvedModel> ForAsync(AiJob job, string fallbackDeployment, CancellationToken cancellationToken)
        {
            Asked.Add((job, fallbackDeployment));
            return Task.FromResult(model);
        }

        public void Invalidate()
        {
        }
    }

    private static readonly ResolvedModel Luna = new("luna-dep", TemperatureMode.Default);
    private static readonly ResolvedModel Usual = new("other-dep", TemperatureMode.Usual);

    private static string Chat(string content) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop", ["message"] = new JsonObject { ["content"] = content } }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15 },
    }.ToJsonString();

    // ---- Recipes

    private static (FoundryRecipeGenerator Generator, Handler Handler, Logs<FoundryRecipeGenerator> Logs) Recipes(
        HttpStatusCode status, string body, IActiveAiModels? models)
    {
        var handler = new Handler(status, body);
        var logs = new Logs<FoundryRecipeGenerator>();
        var options = new RecipeGenerationOptions(Endpoint, "gpt-4o") { Attempts = 1, FirstRetryDelay = TimeSpan.Zero };
        return (new FoundryRecipeGenerator(new HttpClient(handler), options, new Credential(), TimeProvider.System, logs, models), handler, logs);
    }

    private static RecipeRequest RecipeRequest(string? comments = null, IReadOnlyList<string>? avoid = null) =>
        new(RecipeMeal.Dinner, false, [new RecipeStockItem(Guid.NewGuid(), "Semi-skimmed milk", 1, 3, false, "Dairy & eggs", 0)], [],
            comments, avoid ?? []);

    [Fact]
    public async Task Recipes_use_the_active_models_deployment_and_its_usual_temperatures()
    {
        var models = new FixedModels(Usual);
        var (generator, handler, _) = Recipes(HttpStatusCode.OK, Chat("{}"), models);

        await generator.SuggestAsync(RecipeRequest(), CancellationToken.None);
        await generator.SuggestAsync(RecipeRequest(avoid: ["Omelette"]), CancellationToken.None); // Another one

        Assert.All(handler.Requests, r => Assert.Equal("https://ais.example.test/openai/deployments/other-dep/chat/completions?api-version=2024-10-21", r.Uri!.ToString()));
        Assert.Equal(FoundryRecipeGenerator.Temperature, JsonNode.Parse(handler.Requests[0].Body)!["temperature"]!.GetValue<double>());
        Assert.Equal(FoundryRecipeGenerator.AnotherTemperature, JsonNode.Parse(handler.Requests.Last(r => r.Body.Contains("Omelette", StringComparison.Ordinal)).Body)!["temperature"]!.GetValue<double>());
        Assert.Contains((AiJob.Recipes, "gpt-4o"), models.Asked); // the app setting is the fallback
    }

    [Fact]
    public async Task A_model_that_only_takes_its_default_temperature_gets_none_for_every_recipe_feature()
    {
        var (generator, handler, _) = Recipes(HttpStatusCode.OK, Chat("{}"), new FixedModels(Luna));

        await generator.SuggestAsync(RecipeRequest(), CancellationToken.None);
        await generator.SuggestAsync(RecipeRequest(avoid: ["Omelette"]), CancellationToken.None);
        await generator.SuggestQuickIdeasAsync(RecipeRequest(), CancellationToken.None);

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r =>
        {
            Assert.Contains("/deployments/luna-dep/", r.Uri!.AbsolutePath, StringComparison.Ordinal);
            Assert.False(JsonNode.Parse(r.Body)!.AsObject().ContainsKey("temperature"));
            Assert.NotNull(JsonNode.Parse(r.Body)!["response_format"]); // the rest of the request is unchanged
        });
    }

    [Fact]
    public async Task A_refused_recipe_request_is_logged_with_foundrys_reason_and_the_deployment_but_not_the_prompt_and_isnt_retried()
    {
        var (generator, handler, logs) = Recipes(HttpStatusCode.BadRequest, Refusal, new FixedModels(Usual));

        var result = await generator.SuggestAsync(RecipeRequest(comments: "my secret comment about dinner"), CancellationToken.None);

        Assert.IsType<RecipeResult.Unavailable>(result);
        Assert.Single(handler.Requests); // no retry, no fallback
        var (level, message) = Assert.Single(logs.Entries, e => e.Message.Contains("refused", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains("HTTP 400", message, StringComparison.Ordinal);
        Assert.Contains("other-dep", message, StringComparison.Ordinal);
        Assert.Contains("unsupported_value", message, StringComparison.Ordinal);
        Assert.Contains("'temperature' does not support 0.7", message, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("secret comment", StringComparison.Ordinal) || e.Message.Contains("Semi-skimmed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recipes_log_the_deployment_that_answered()
    {
        var (generator, _, logs) = Recipes(HttpStatusCode.OK, Chat("{}"), new FixedModels(Luna));

        await generator.SuggestAsync(RecipeRequest(), CancellationToken.None);

        Assert.Contains(logs.Entries, e => e.Message.Contains("on luna-dep", StringComparison.Ordinal));
    }

    // ---- Photos

    private static (FoundryPhotoRecogniser Recogniser, Handler Handler, Logs<FoundryPhotoRecogniser> Logs) Photos(
        HttpStatusCode status, string body, IActiveAiModels? models)
    {
        var handler = new Handler(status, body);
        var logs = new Logs<FoundryPhotoRecogniser>();
        var options = new PhotoRecognitionOptions(Endpoint, "gpt-4o") { Attempts = 1, FirstRetryDelay = TimeSpan.Zero };
        return (new FoundryPhotoRecogniser(new HttpClient(handler), options, new Credential(), TimeProvider.System, logs, models), handler, logs);
    }

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    [Fact]
    public async Task Photos_use_their_jobs_model_with_temperature_0_or_none()
    {
        var usual = new FixedModels(Usual);
        var (recogniser, handler, _) = Photos(HttpStatusCode.OK, Chat("""{"readable":true,"items":[]}"""), usual);
        await recogniser.RecogniseAsync(Jpeg, CancellationToken.None);

        Assert.Contains("/deployments/other-dep/", Assert.Single(handler.Requests).Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(0, JsonNode.Parse(handler.Requests[0].Body)!["temperature"]!.GetValue<double>());
        Assert.Equal([(AiJob.Photos, "gpt-4o")], usual.Asked);

        var (second, secondHandler, logs) = Photos(HttpStatusCode.OK, Chat("""{"readable":true,"items":[]}"""), new FixedModels(Luna));
        await second.RecogniseAsync(Jpeg, CancellationToken.None);
        Assert.False(JsonNode.Parse(Assert.Single(secondHandler.Requests).Body)!.AsObject().ContainsKey("temperature"));
        Assert.Contains(logs.Entries, e => e.Message.Contains("luna-dep", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_photo_request_is_logged_with_foundrys_reason()
    {
        var (recogniser, handler, logs) = Photos(HttpStatusCode.BadRequest, Refusal, new FixedModels(Usual));

        Assert.IsType<CupboardTracker.Application.Adding.PhotoRecognitionResult.Unavailable>(await recogniser.RecogniseAsync(Jpeg, CancellationToken.None));

        Assert.Single(handler.Requests);
        var entry = Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("other-dep", entry.Message, StringComparison.Ordinal);
        Assert.Contains("unsupported_value", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/9j/", entry.Message, StringComparison.Ordinal); // never the image
    }

    // ---- Generic names

    private static (FoundryGenericNameWriter Writer, Handler Handler, Logs<FoundryGenericNameWriter> Logs) Names(
        HttpStatusCode status, string body, IActiveAiModels? models)
    {
        var handler = new Handler(status, body);
        var logs = new Logs<FoundryGenericNameWriter>();
        var options = new GenericNameWriterOptions(Endpoint, "gpt-4o", TimeSpan.Zero) { Attempts = 1, FirstRetryDelay = TimeSpan.Zero };
        return (new FoundryGenericNameWriter(new HttpClient(handler), options, new FoundryThrottle(options, TimeProvider.System), new Credential(),
            TimeProvider.System, logs, models), handler, logs);
    }

    private static readonly ProductDescription Milk = new("SEMI-SKIMMED BRITISH MILK", "Dairy Manor", "4 pints", ["en:milks"]);

    [Fact]
    public async Task Generic_names_use_their_jobs_model_with_temperature_0_or_none()
    {
        var answer = Chat("""{"genericName":"Semi-skimmed milk"}""");
        var usual = new FixedModels(Usual);
        var (writer, handler, _) = Names(HttpStatusCode.OK, answer, usual);
        Assert.IsType<GenericWriteResult.Written>(await writer.WriteAsync(Milk, "Dairy & eggs", [], CancellationToken.None));

        Assert.Contains("/deployments/other-dep/", Assert.Single(handler.Requests).Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(0, JsonNode.Parse(handler.Requests[0].Body)!["temperature"]!.GetValue<double>());
        Assert.Equal([(AiJob.GenericNames, "gpt-4o")], usual.Asked);

        var (second, secondHandler, logs) = Names(HttpStatusCode.OK, answer, new FixedModels(Luna));
        await second.WriteAsync(Milk, "Dairy & eggs", [], CancellationToken.None);
        Assert.False(JsonNode.Parse(Assert.Single(secondHandler.Requests).Body)!.AsObject().ContainsKey("temperature"));
        Assert.Contains(logs.Entries, e => e.Message.Contains("luna-dep", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_generic_name_request_is_now_logged_with_foundrys_reason()
    {
        var (writer, _, logs) = Names(HttpStatusCode.BadRequest, Refusal, new FixedModels(Usual));

        Assert.IsType<GenericWriteResult.Failed>(await writer.WriteAsync(Milk, "Dairy & eggs", [], CancellationToken.None));

        var entry = Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("unsupported_value", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SEMI-SKIMMED", entry.Message, StringComparison.Ordinal);
    }

    // ---- Reading Foundry's reason

    [Fact]
    public void Foundrys_code_and_message_are_read_from_the_error_body()
    {
        Assert.Equal(("unsupported_value", "Unsupported value: 'temperature' does not support 0.7 with this model. Only the default (1) value is supported."),
            FoundryErrors.Reason(Refusal));
    }

    [Fact]
    public void A_long_multi_line_message_is_cut_and_put_on_one_line()
    {
        var body = new JsonObject { ["error"] = new JsonObject { ["code"] = "BadRequest", ["message"] = "first line\n  second\tline " + new string('x', 400) } }.ToJsonString();

        var (code, message) = FoundryErrors.Reason(body);

        Assert.Equal("BadRequest", code);
        Assert.StartsWith("first line second line xxx", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', message);
        Assert.Equal(FoundryErrors.MaxMessageLength + 1, message.Length); // the text plus an ellipsis
        Assert.EndsWith("…", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("""{"message":"no error object"}""")]
    [InlineData("""{"error":"a string"}""")]
    [InlineData("""{"error":{}}""")]
    public void Anything_that_isnt_a_foundry_error_gives_unknown(string? body) =>
        Assert.Equal(("unknown", "no message"), FoundryErrors.Reason(body));

    [Fact]
    public void A_models_temperature_is_the_callers_usual_one_or_none()
    {
        Assert.Equal(0.7, Usual.Temperature(0.7));
        Assert.Equal(0.0, Usual.Temperature(0));
        Assert.Null(Luna.Temperature(0.7));
        Assert.Null(Luna.Temperature(0));
    }

    // ---- The stored document

    [Fact]
    public void The_settings_document_round_trips_and_leaves_out_nothing_the_app_reads()
    {
        var added = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        var by = Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67");
        var settings = new AiModelSettings(
            [
                new AiModel("gpt-4o", "GPT-4o", "gpt-4o", TemperatureMode.Usual, [AiJob.Recipes, AiJob.Photos, AiJob.GenericNames], by, "Gary", added),
                new AiModel("luna", "GPT-6 Luna", "gpt-6-luna", TemperatureMode.Default, [AiJob.Recipes], by, "Gary", added,
                    new JsonObject { ["ranAt"] = "2026-10-27T09:05:00.000Z", ["checks"] = new JsonArray(new JsonObject { ["name"] = "reachable", ["result"] = "pass" }) }),
            ],
            new Dictionary<AiJob, ActiveSelection>
            {
                [AiJob.Recipes] = new("luna", by, "Gary", added.AddMinutes(10), "gpt-4o"),
                [AiJob.Photos] = new("gpt-4o", by, "Gary", added.AddMinutes(11), null),
            });

        var document = JsonNode.Parse(CosmosAiModelSettingsRepository.ToDocument(settings).ToJsonString())!.AsObject();
        var back = CosmosAiModelSettingsRepository.FromDocument(document);

        Assert.Equal(("aiModels", "aiModels"), (document["id"]!.GetValue<string>(), document["type"]!.GetValue<string>()));
        Assert.Equal("default", document["models"]![1]!["temperatureMode"]!.GetValue<string>());
        Assert.Equal(["recipes"], document["models"]![1]!["jobs"]!.AsArray().Select(j => j!.GetValue<string>()));
        Assert.Equal(["gpt-4o", "luna"], back.Models.Select(m => m.Id));
        Assert.Equal((TemperatureMode.Default, "gpt-6-luna"), (back.Models[1].TemperatureMode, back.Models[1].Deployment));
        Assert.Equal([AiJob.Recipes], back.Models[1].Jobs);
        Assert.Equal("pass", back.Models[1].Tests!["checks"]![0]!["result"]!.GetValue<string>());
        Assert.Equal(("luna", "gpt-4o", added.AddMinutes(10)), (back.Active[AiJob.Recipes].ModelId, back.Active[AiJob.Recipes].PreviousModelId, back.Active[AiJob.Recipes].ChangedAt));
        Assert.Null(back.Active[AiJob.Photos].PreviousModelId);
        Assert.False(back.Active.ContainsKey(AiJob.GenericNames));
    }

    [Fact]
    public void An_unknown_or_missing_field_reads_as_a_safe_default()
    {
        var document = JsonNode.Parse("""{"id":"aiModels","models":[{"id":"m","label":"M","deployment":"d","temperatureMode":"surprising","jobs":["recipes","nonsense"]}],"active":{"recipes":{"modelId":"m"}}}""")!.AsObject();

        var settings = CosmosAiModelSettingsRepository.FromDocument(document);

        Assert.Equal(TemperatureMode.Usual, settings.Models[0].TemperatureMode);
        Assert.Equal([AiJob.Recipes], settings.Models[0].Jobs);
        Assert.Equal("m", settings.Active[AiJob.Recipes].ModelId);
    }
}
