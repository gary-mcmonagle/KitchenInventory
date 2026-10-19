using System.Net;
using System.Text.Json.Nodes;
using Azure.Core;
using CupboardTracker.Application.GenericNaming;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Domain.Products;
using CupboardTracker.Infrastructure.Cosmos;
using CupboardTracker.Infrastructure.GenericNaming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CupboardTracker.Infrastructure.Tests;

public sealed class GenericNameTests
{
    private static readonly GenericName[] Names =
    [
        GenericName.Create("Semi-skimmed milk", "dairyEggs", DateTimeOffset.UnixEpoch, "foundry"),
        GenericName.Create("Mature cheddar", "dairyEggs", DateTimeOffset.UnixEpoch, "foundry"),
    ];

    private static readonly ProductDescription Milk = new("SEMI-SKIMMED BRITISH MILK", "Dairy Manor", "4 pints", ["en:dairies", "en:milks", "en:semi-skimmed-milks"]);

    [Fact]
    public void The_jev_question_offers_the_names_and_none_with_the_calibrated_wording()
    {
        var question = JevGenericNameMatcher.Question(Names)["name"]!;

        Assert.Equal("choice", question["type"]!.GetValue<string>());
        Assert.Equal(JevGenericNameMatcher.Instructions, question["instructions"]!.GetValue<string>());
        var criteria = question["criteria"]!.AsObject();
        Assert.Equal(["n0", "n1", "none"], criteria.Select(c => c.Key));
        Assert.Equal("Semi-skimmed milk", criteria["n0"]!.GetValue<string>());
    }

    [Fact]
    public void Jevs_answer_is_read_as_a_match_none_or_a_failure()
    {
        static JsonObject Answer(string choice, double confidence = 0.97) =>
            new() { ["name"] = new JsonObject { ["choice"] = choice, ["confidence"] = confidence } };

        var matched = Assert.IsType<GenericMatchResult.Matched>(JevGenericNameMatcher.Read(Answer("n1", 0.93), Names));
        Assert.Equal(("mature cheddar", 0.93), (matched.Name.Id, matched.Confidence));
        Assert.IsType<GenericMatchResult.NoMatch>(JevGenericNameMatcher.Read(Answer("none"), Names));
        Assert.IsType<GenericMatchResult.Failed>(JevGenericNameMatcher.Read(Answer("n9"), Names));
        Assert.IsType<GenericMatchResult.Failed>(JevGenericNameMatcher.Read([], Names));
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("test-token-not-real", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class Handler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int next;

        public List<(DateTimeOffset At, string Body)> Requests { get; } = [];

        public TimeProvider Clock { get; set; } = TimeProvider.System;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((Clock.GetUtcNow(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return responses[Math.Min(next++, responses.Length - 1)]();
        }
    }

    /// <summary>A recorded chat completions response.</summary>
    private static HttpResponseMessage Written(string name) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = new JsonObject { ["genericName"] = name }.ToJsonString() } }),
            ["usage"] = new JsonObject { ["total_tokens"] = 640 },
        }.ToJsonString()),
    };

    private static (FoundryGenericNameWriter Writer, Handler Handler) Writer(TimeSpan minInterval, TimeProvider clock, params Func<HttpResponseMessage>[] responses)
    {
        var options = new GenericNameWriterOptions(new Uri("https://ais.example.test/"), "gpt-4o", minInterval) { FirstRetryDelay = TimeSpan.Zero };
        var handler = new Handler(responses) { Clock = clock };
        return (new FoundryGenericNameWriter(new HttpClient(handler), options, new FoundryThrottle(options, clock), new Credential(), clock,
            NullLogger<FoundryGenericNameWriter>.Instance), handler);
    }

    [Fact]
    public async Task Foundry_is_asked_with_the_level_a_rules_a_strict_schema_and_the_aisles_names_as_examples()
    {
        var (writer, handler) = Writer(TimeSpan.Zero, TimeProvider.System, () => Written("Semi-skimmed milk"));

        var result = await writer.WriteAsync(Milk, "Dairy & eggs", ["Mature cheddar", "Greek yogurt"], CancellationToken.None);

        Assert.Equal("Semi-skimmed milk", Assert.IsType<GenericWriteResult.Written>(result).Name);
        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body)!;
        Assert.Equal(FoundryGenericNameWriter.Instructions, body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Contains("Level: SHOPPER", FoundryGenericNameWriter.Instructions, StringComparison.Ordinal);
        var prompt = body["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("Product name: SEMI-SKIMMED BRITISH MILK", prompt, StringComparison.Ordinal);
        Assert.Contains("Categories: dairies, milks, semi skimmed milks", prompt, StringComparison.Ordinal);
        Assert.Contains("Aisle: Dairy & eggs", prompt, StringComparison.Ordinal);
        Assert.Contains("Names already used in this aisle: Mature cheddar, Greek yogurt", prompt, StringComparison.Ordinal);
        Assert.True(body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.Equal(0, body["temperature"]!.GetValue<int>());
    }

    [Fact]
    public async Task Foundry_calls_are_spaced_by_the_throttle()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var (writer, handler) = Writer(TimeSpan.FromSeconds(6), clock, () => Written("A"), () => Written("B"));

        await writer.WriteAsync(Milk, "Dairy & eggs", [], CancellationToken.None);
        var second = writer.WriteAsync(Milk, "Dairy & eggs", [], CancellationToken.None);
        Assert.Single(handler.Requests); // waiting for the throttle
        clock.Advance(TimeSpan.FromSeconds(6));
        await second;

        Assert.Equal(TimeSpan.FromSeconds(6), handler.Requests[1].At - handler.Requests[0].At);
    }

    [Fact]
    public async Task Throttling_and_server_errors_are_retried_and_refusals_and_missing_config_are_reported()
    {
        var (retried, retriedHandler) = Writer(TimeSpan.Zero, TimeProvider.System, () => new HttpResponseMessage(HttpStatusCode.TooManyRequests), () => Written("Leeks"));
        Assert.IsType<GenericWriteResult.Written>(await retried.WriteAsync(Milk, "Fruit & veg", [], CancellationToken.None));
        Assert.Equal(2, retriedHandler.Requests.Count);

        var (refused, _) = Writer(TimeSpan.Zero, TimeProvider.System, () => new HttpResponseMessage(HttpStatusCode.Forbidden));
        Assert.Equal("HTTP 403", Assert.IsType<GenericWriteResult.Failed>(await refused.WriteAsync(Milk, "x", [], CancellationToken.None)).Reason);

        var (down, _) = Writer(TimeSpan.Zero, TimeProvider.System, () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.IsType<GenericWriteResult.Unavailable>(await down.WriteAsync(Milk, "x", [], CancellationToken.None));

        var unconfigured = new FoundryGenericNameWriter(new HttpClient(), new GenericNameWriterOptions(null, "gpt-4o", TimeSpan.Zero),
            new FoundryThrottle(new GenericNameWriterOptions(null, "gpt-4o", TimeSpan.Zero), TimeProvider.System), new Credential(), TimeProvider.System,
            NullLogger<FoundryGenericNameWriter>.Instance);
        Assert.IsType<GenericWriteResult.NotConfigured>(await unconfigured.WriteAsync(Milk, "x", [], CancellationToken.None));
    }

    [Fact]
    public void Settings_come_from_app_settings_with_safe_defaults()
    {
        static IConfiguration With(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Equal(new GenericNameSettings(0.9, 254), ServiceCollectionExtensions.GenericNameSettingsFromConfiguration(With([])));
        Assert.Equal(new GenericNameSettings(0.8, 3), ServiceCollectionExtensions.GenericNameSettingsFromConfiguration(
            With(new() { ["GENERIC_NAME_MATCH_MIN_CONFIDENCE"] = "0.8", ["GENERIC_NAME_BATCH_SIZE"] = "3" })));
        Assert.Equal(254, ServiceCollectionExtensions.GenericNameSettingsFromConfiguration(With(new() { ["GENERIC_NAME_BATCH_SIZE"] = "999" })).BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(6), GenericNameWriterOptions.FromConfiguration(With([])).MinInterval);
        Assert.Equal("gpt-4o", GenericNameWriterOptions.FromConfiguration(With([])).Deployment);
    }

    [Fact]
    public void Records_are_generic_name_documents_in_the_products_container_and_round_trip()
    {
        var name = GenericName.Create("Semi-skimmed milk", "dairyEggs", new DateTimeOffset(2026, 10, 19, 21, 0, 0, TimeSpan.Zero), "foundry");

        var document = CosmosGenericNameRepository.ToDocument(name);

        Assert.Equal(("generic:semi-skimmed milk", "genericName"), (document["id"]!.GetValue<string>(), document["type"]!.GetValue<string>()));
        Assert.Equal(name, CosmosGenericNameRepository.FromDocument(JsonNode.Parse(document.ToJsonString())!.AsObject()));
        Assert.Null(CosmosGenericNameRepository.FromDocument(new JsonObject { ["id"] = "barcode:123", ["type"] = "barcodeLookup" }));
    }
}
