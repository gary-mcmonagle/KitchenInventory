using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Domain.Products;
using CupboardTracker.Infrastructure.Cosmos;
using CupboardTracker.Infrastructure.Jev;
using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure.Tests;

public sealed class JevTests
{
    private const string Key = "test-key-not-a-real-one";

    private static readonly JevOptions Options =
        new(new Uri("https://jev.example.test"), "jev-1.13.0", Key) { FirstRetryDelay = TimeSpan.Zero };

    // Recorded from jev-1.13.0 on 2026-10-18 with the spike #40 questions (usage removed).
    private const string Naan = """
        {"model":"jev-1.13.0","answers":{"shelf_life":{"type":"score","score":2.83,"confidence":0.16,
         "probabilities":{"0":0.14,"1":0.2,"2":0.17,"3":0.09,"4":0.11,"5":0.2,"6":0.07,"7":0.02,"8":0.0,"9":0.0}},
         "storage":{"type":"choice","choice":"freezer","confidence":0.55,"probabilities":{"cupboard":0.23,"fridge":0.07,"freezer":0.7}}}}
        """;

    private const string Milk = """
        {"model":"jev-1.13.0","answers":{"shelf_life":{"type":"score","score":2.56,"confidence":0.74,
         "probabilities":{"0":0.01,"1":0.03,"2":0.52,"3":0.33,"4":0.08,"5":0.01,"6":0.01,"7":0.01,"8":0.0,"9":0.0}},
         "storage":{"type":"choice","choice":"fridge","confidence":0.86,"probabilities":{"cupboard":0.09,"fridge":0.91,"freezer":0.0}}}}
        """;

    private sealed class Handler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int next;

        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return responses[Math.Min(next++, responses.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Status(int status, string body = "{}") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static (JevShelfLifeEstimator Estimator, Handler Handler) Make(
        JevOptions? options = null, params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new Handler(responses);
        var o = options ?? Options;
        return (new JevShelfLifeEstimator(new JevClient(new HttpClient(handler), o, TimeProvider.System), o), handler);
    }

    private static readonly ProductDescription NaanProduct =
        new("PLAIN NAAN BREADS", "Taste of", "2 x 130 g", ["en:breads", "en:naans"]);

    [Fact]
    public async Task Sends_the_spike_questions_with_the_pinned_model_and_the_key_only_in_the_header()
    {
        var (estimator, handler) = Make(null, _ => Status(200, Milk));

        await estimator.EstimateAsync(NaanProduct, CancellationToken.None);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://jev.example.test/v1/systemone", request.RequestUri!.ToString());
        Assert.Equal(("Bearer", Key), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.DoesNotContain(Key, body, StringComparison.Ordinal);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("jev-1.13.0", json["model"]!.GetValue<string>());
        Assert.Equal("""{"name":"PLAIN NAAN BREADS","brand":"Taste of","size":"2 x 130 g","categories":["en:breads","en:naans"]}""",
            json["state"]!.ToJsonString());
        Assert.Equal("score", json["questions"]!["shelf_life"]!["type"]!.GetValue<string>());
        Assert.Equal(10, json["questions"]!["shelf_life"]!["criteria"]!.AsArray().Count);
        Assert.Equal("choice", json["questions"]!["storage"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Only_what_we_have_goes_in_the_state() =>
        Assert.Equal("""{"name":"Bananas"}""", JevShelfLifeEstimator.State(new ProductDescription("Bananas", null, null, [])).ToJsonString());

    [Fact]
    public async Task Confident_answers_use_the_median_level()
    {
        var (estimator, _) = Make(null, _ => Status(200, Milk));

        var result = await estimator.EstimateAsync(NaanProduct, CancellationToken.None);

        // Running total 0.01, 0.04, 0.56: the median is "About a week".
        Assert.Equal(new ShelfLifeEstimate(7, Storage.Fridge, 0.74, false), Assert.IsType<ShelfLifeResult.Estimated>(result).Estimate);
    }

    [Fact]
    public void Low_confidence_answers_use_the_25th_percentile()
    {
        var result = JevShelfLifeEstimator.Read(JsonNode.Parse(Naan)!["answers"]!.AsObject());

        // Running total 0.14, 0.34: the cautious level is "3-4 days" (the median would be "About a week").
        Assert.Equal(new ShelfLifeEstimate(4, Storage.Freezer, 0.16, true), Assert.IsType<ShelfLifeResult.Estimated>(result).Estimate);
    }

    [Theory]
    [InlineData(0.49, true, 4)]
    [InlineData(0.5, false, 7)]
    public void The_threshold_is_confidence_one_half(double confidence, bool low, int days)
    {
        var answers = JsonNode.Parse(Naan)!["answers"]!.AsObject();
        answers["shelf_life"]!["confidence"] = confidence;

        var estimate = Assert.IsType<ShelfLifeResult.Estimated>(JevShelfLifeEstimator.Read(answers)).Estimate;

        Assert.Equal((low, days), (estimate.LowConfidence, estimate.Days));
    }

    [Fact]
    public void Each_level_maps_to_days() =>
        Assert.Equal([2, 4, 7, 14, 30, 90, 180, 365, 730, 1095], JevShelfLifeEstimator.Levels.Select(l => l.Days));

    [Fact]
    public void Percentiles()
    {
        Assert.Equal(0, JevShelfLifeEstimator.Percentile([1, 0, 0], 0.5));
        Assert.Equal(2, JevShelfLifeEstimator.Percentile([0, 0, 1], 0.25));
        Assert.Equal(1, JevShelfLifeEstimator.Percentile([0.25, 0.25, 0.5], 0.5)); // reaching exactly 0.5 counts
        Assert.Equal(1, JevShelfLifeEstimator.Percentile([1, 1, 2], 0.5));        // unnormalised still works
    }

    [Fact]
    public void An_answer_without_a_shelf_life_is_a_failure() =>
        Assert.IsType<ShelfLifeResult.Failed>(JevShelfLifeEstimator.Read(new JsonObject()));

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    [InlineData(503)]
    public async Task Retries_rate_limits_overload_and_server_errors(int status)
    {
        var (estimator, handler) = Make(null, _ => Status(status), _ => Status(200, Milk));

        Assert.IsType<ShelfLifeResult.Estimated>(await estimator.EstimateAsync(NaanProduct, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Gives_up_after_three_attempts_as_unavailable()
    {
        var (estimator, handler) = Make(null, _ => Status(529));

        var result = await estimator.EstimateAsync(NaanProduct, CancellationToken.None);

        Assert.Equal("HTTP 529 (after 3 attempts)", Assert.IsType<ShelfLifeResult.Unavailable>(result).Reason);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(413)]
    [InlineData(415)]
    [InlineData(422)]
    public async Task Never_retries_requests_jev_refuses(int status)
    {
        var (estimator, handler) = Make(null, _ => Status(status, """{"detail":"nope"}"""));

        var result = await estimator.EstimateAsync(NaanProduct, CancellationToken.None);

        Assert.Equal($"HTTP {status}", Assert.IsType<ShelfLifeResult.Failed>(result).Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Network_errors_are_retried_then_unavailable()
    {
        var (estimator, _) = Make(null, _ => throw new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable));
        Assert.IsType<ShelfLifeResult.Unavailable>(await estimator.EstimateAsync(NaanProduct, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@Microsoft.KeyVault(VaultName=kv-cupboard-ugxum7;SecretName=jev-api-key)")]
    public async Task Without_a_resolved_key_it_is_not_configured_and_never_calls(string? key)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["JEV_API_KEY"] = key }).Build();
        var options = JevOptions.FromConfiguration(config);
        var (estimator, handler) = Make(options, _ => Status(200, Milk));

        Assert.IsType<ShelfLifeResult.NotConfigured>(await estimator.EstimateAsync(NaanProduct, CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.Equal(("https://api.typesafe.ai/", "jev-1.13.0"), (options.BaseUrl.ToString(), options.Model));
    }

    [Fact]
    public void The_options_never_show_the_key() =>
        Assert.DoesNotContain(Key, Options.ToString(), StringComparison.Ordinal);

    [Fact]
    public void A_shelf_life_round_trips_through_the_product_document()
    {
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 18, 7, 0, 0, TimeSpan.Zero);
        var product = Product.Found(id, "20055363", new ProductDetails("PLAIN NAAN BREADS", null, null, null, []), at, 1)
            .WithShelfLife(new ShelfLife(4, Storage.Freezer, 0.16, true, "jev", at));

        var document = ProductDocument.Write(product, null);
        var stored = JsonNode.Parse(document.ToJsonString())!.AsObject();

        Assert.Equal((4, "jev", 0.16, true, "freezer", "2026-10-18T07:00:00.000Z"),
            (stored["shelfLifeDays"]!.GetValue<int>(), stored["shelfLifeSource"]!.GetValue<string>(),
             stored["shelfLifeConfidence"]!.GetValue<double>(), stored["jevLowConfidence"]!.GetValue<bool>(),
             stored["storage"]!.GetValue<string>(), stored["shelfLifeEstimatedAt"]!.GetValue<string>()));
        Assert.Equal(product.ShelfLife, ProductDocument.ToProduct(stored).ShelfLife);
        Assert.Null(ProductDocument.ToProduct(ProductDocument.Write(product with { ShelfLife = null }, null)).ShelfLife);
    }

    // Recorded from jev-1.13.0 on 2026-10-18 for #65's check (docs/spikes/65-jev-unit-state.py), usage and legends removed.
    private const string NaanStates = """
        {"model":"jev-1.13.0","answers":{
         "opened_storage":{"type":"choice","choice":"cupboard","confidence":0.22,"probabilities":{"cupboard":0.48,"fridge":0.27,"freezer":0.25}},
         "opened_shelf_life":{"type":"score","score":2.24,"confidence":0.32,"probabilities":{"0":0.25,"1":0.26,"2":0.16,"3":0.08,"4":0.07,"5":0.07,"6":0.05,"7":0.04,"8":0.01,"9":0.01}},
         "frozen_shelf_life":{"type":"score","score":5.12,"confidence":0.79,"probabilities":{"0":0.01,"1":0.0,"2":0.0,"3":0.02,"4":0.11,"5":0.61,"6":0.19,"7":0.05,"8":0.01,"9":0.0}},
         "defrosted_storage":{"type":"choice","choice":"cupboard","confidence":0.18,"probabilities":{"cupboard":0.59,"fridge":0.41}},
         "defrosted_shelf_life":{"type":"score","score":1.22,"confidence":0.63,"probabilities":{"0":0.37,"1":0.41,"2":0.1,"3":0.03,"4":0.02,"5":0.04,"6":0.01,"7":0.02,"8":0.0,"9":0.0}}}}
        """;

    [Fact]
    public async Task State_questions_go_in_one_request_with_the_usual_storage()
    {
        var (estimator, handler) = Make(null, _ => Status(200, NaanStates));

        var result = await estimator.EstimateStatesAsync(NaanProduct, Storage.Cupboard, CancellationToken.None);

        Assert.IsType<StateEstimatesResult.Estimated>(result);
        var (_, body) = Assert.Single(handler.Requests);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("cupboard", json["state"]!["storage"]!.GetValue<string>());
        Assert.Equal(["opened_storage", "opened_shelf_life", "frozen_shelf_life", "defrosted_storage", "defrosted_shelf_life"],
            json["questions"]!.AsObject().Select(q => q.Key));
        Assert.Equal(["cupboard", "fridge"], json["questions"]!["defrosted_storage"]!["criteria"]!.AsObject().Select(c => c.Key));
        Assert.Equal(10, json["questions"]!["frozen_shelf_life"]!["criteria"]!.AsArray().Count);
    }

    [Fact]
    public void State_answers_use_the_same_level_rule()
    {
        var states = Assert.IsType<StateEstimatesResult.Estimated>(
            JevShelfLifeEstimator.ReadStates(JsonNode.Parse(NaanStates)!["answers"]!.AsObject())).Estimates;

        // Opened: confidence 0.32, so the 25th percentile ("1-2 days"); frozen and defrosted: the median.
        Assert.Equal(new StateEstimate(2, Storage.Cupboard, 0.32, true), states.Opened);
        Assert.Equal(new StateEstimate(90, Storage.Freezer, 0.79, false), states.Frozen);
        Assert.Equal(new StateEstimate(4, Storage.Cupboard, 0.63, false), states.Defrosted);
    }

    [Fact]
    public void A_missing_state_answer_is_a_failure()
    {
        var answers = JsonNode.Parse(NaanStates)!["answers"]!.AsObject();
        answers.Remove("frozen_shelf_life");

        Assert.IsType<StateEstimatesResult.Failed>(JevShelfLifeEstimator.ReadStates(answers));
    }

    [Fact]
    public async Task Aisle_and_icon_go_in_one_request_asking_only_whats_wanted()
    {
        var (estimator, handler) = Make(null, _ => Status(200, """
            {"model":"jev-1.13.0","answers":{"aisle":{"type":"choice","choice":"bakery","confidence":0.91},"icon":{"type":"choice","choice":"bread","confidence":0.83}}}
            """));

        var result = await estimator.ClassifyAsync(NaanProduct, Storage.Cupboard, aisle: true, icon: true, CancellationToken.None);

        Assert.Equal(new Classification("bakery", 0.91, "bread", 0.83), Assert.IsType<ClassificationResult.Classified>(result).Classification);
        var json = JsonNode.Parse(Assert.Single(handler.Requests).Body)!;
        Assert.Equal(14, json["questions"]!["aisle"]!["criteria"]!.AsObject().Count);
        Assert.Equal(131, json["questions"]!["icon"]!["criteria"]!.AsObject().Count);
        Assert.Equal("cupboard", json["state"]!["storage"]!.GetValue<string>());
        Assert.Equal(["icon"], JevShelfLifeEstimator.ClassificationQuestions(aisle: false, icon: true).Select(q => q.Key));
    }

    [Fact]
    public void An_aisle_answer_that_isnt_an_aisle_is_a_failure() =>
        Assert.IsType<ClassificationResult.Failed>(JevShelfLifeEstimator.ReadClassification(
            JsonNode.Parse("""{"aisle":{"type":"choice","choice":"garden","confidence":0.9}}""")!.AsObject()));
}
