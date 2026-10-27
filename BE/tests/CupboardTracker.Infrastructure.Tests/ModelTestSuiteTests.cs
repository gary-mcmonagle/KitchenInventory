using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using CupboardTracker.Application.AiModels;
using CupboardTracker.Infrastructure.GenericNaming;
using CupboardTracker.Infrastructure.ModelTesting;
using CupboardTracker.Infrastructure.Photos;
using CupboardTracker.Infrastructure.Recipes;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Infrastructure.Tests;

/// <summary>The model test suite's checks (#121) against recorded Foundry responses: pass, warn and fail paths, with the app's own
/// callers and validators doing the work.</summary>
public sealed class ModelTestSuiteTests
{
    private static readonly Uri Endpoint = new("https://ais.example.test/");

    private const string TemperatureRefusal =
        """{"error":{"code":"unsupported_value","message":"Unsupported value: 'temperature' does not support 0.7 with this model. Only the default (1) value is supported.","param":"temperature"}}""";

    private const string NotFound = """{"error":{"code":"DeploymentNotFound","message":"The API deployment for this resource does not exist."}}""";

    private const string NoImages = """{"error":{"code":"invalid_request_error","message":"Image input is not supported by this model."}}""";

    private const string NoSchemas = """{"error":{"code":"invalid_request_error","message":"response_format of type json_schema is not supported."}}""";

    // What a model says. None of this text may appear in a result or a log.
    private const string RecipeText = "Stir the mature chilli stew";

    private static readonly string Recipe = $$"""
        {"title":"Two-pan tomato pasta","summary":"One base, two finishes.","meal":"dinner","servings":2,"servingsMeat":1,"servingsVeg":1,
         "totalMinutes":25,"equipment":["hob"],
         "ingredients":[
           {"name":"Pasta","amount":"200 g","productId":"p7","for":"all"},
           {"name":"Chopped tomatoes","amount":"1 tin","productId":"p6","for":"all"},
           {"name":"Chicken thighs","amount":"2","productId":"p1","for":"meat"},
           {"name":"Cheddar","amount":"80 g","productId":"p3","for":"veg"}],
         "steps":[{"text":"Cook the pasta.","for":"all"},{"text":"{{RecipeText}} with the tomatoes.","for":"all"},{"text":"Fry the chicken and stir it into one half.","for":"meat"},{"text":"Stir the cheddar into the other half.","for":"veg"}],
         "usesUp":[],
         "alsoNeeded":[]}
        """;

    private static readonly string Adjusted = Recipe.Replace(
        """{"name":"Cheddar","amount":"80 g","productId":"p3","for":"veg"}""",
        """{"name":"Cheddar","amount":"80 g","productId":"p3","for":"veg"},{"name":"Chilli flakes","amount":"1 tsp","productId":null,"for":"all"}""",
        StringComparison.Ordinal);

    private const string Ideas = """
        {"ideas":[
          {"title":"Cheese on toast","how":"Toast it.","appliance":"toaster","minutes":5,"uses":["p3"],"alsoNeeded":[]},
          {"title":"Beans in the microwave","how":"Heat them.","appliance":"microwave","minutes":4,"uses":["p11"],"alsoNeeded":[]},
          {"title":"Scrambled egg in a mug","how":"Whisk and zap.","appliance":"microwave","minutes":3,"uses":["p4"],"alsoNeeded":[]}]}
        """;

    private const string Photo = """{"readable":true,"items":[{"name":"Tomato soup","brand":null,"size":"400g","kind":"packaged","quantity":1}]}""";

    private const string Name = """{"genericName":"Baked beans"}""";

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token-not-real", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    /// <summary>A pretend Foundry deployment that answers by what it's asked for, and refuses what the scenario says it can't do.</summary>
    private sealed class Foundry : HttpMessageHandler
    {
        public bool RefuseTemperature { get; init; }

        public bool RefuseImages { get; init; }

        public bool RefuseSchemas { get; init; }

        public bool Missing { get; init; }

        public int CompletionTokens { get; init; } = 100;

        public List<(string Deployment, JsonObject Body)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.Content!.ReadAsStringAsync(cancellationToken).Result;
            var body = JsonNode.Parse(text)!.AsObject();
            var deployment = request.RequestUri!.Segments[3].TrimEnd('/');
            lock (Requests)
            {
                Requests.Add((deployment, body));
            }

            if (Missing)
            {
                return Task.FromResult(Reply(HttpStatusCode.NotFound, NotFound));
            }

            if (RefuseTemperature && body.ContainsKey("temperature"))
            {
                return Task.FromResult(Reply(HttpStatusCode.BadRequest, TemperatureRefusal));
            }

            if (RefuseImages && text.Contains("image_url", StringComparison.Ordinal))
            {
                return Task.FromResult(Reply(HttpStatusCode.BadRequest, NoImages));
            }

            if (RefuseSchemas && body.ContainsKey("response_format"))
            {
                return Task.FromResult(Reply(HttpStatusCode.BadRequest, NoSchemas));
            }

            var schema = (string?)body["response_format"]?["json_schema"]?["name"];
            var content = schema switch
            {
                "recipe" => text.Contains("chilli flakes", StringComparison.OrdinalIgnoreCase) ? Adjusted : Recipe,
                "kitchen_photo" => Photo,
                "generic_name" => Name,
                null => "ready",
                _ => Ideas,
            };
            return Task.FromResult(Reply(HttpStatusCode.OK, new JsonObject
            {
                ["model"] = deployment + "-2026-01-01",
                ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop", ["message"] = new JsonObject { ["content"] = content } }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 1000, ["completion_tokens"] = CompletionTokens },
            }.ToJsonString()));
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(Foundry handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Image : IModelTestImageStore
    {
        public Task<byte[]> GetAsync(CancellationToken cancellationToken) => Task.FromResult(TestPackImage.Create());
    }

    private sealed class Prices(ModelPrice? price) : IModelPrices
    {
        public List<IReadOnlyList<string>> Asked { get; } = [];

        public Task<ModelPrice?> LookupAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
        {
            Asked.Add(names);
            return Task.FromResult(price);
        }
    }

    private sealed class Logs : ILoggerFactory
    {
        public List<string> Messages { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Sink(Messages);

        public void Dispose()
        {
        }

        private sealed class Sink(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }

    private static readonly ModelPrice Price = new(1m, 2m, "test meters");

    private static (FoundryModelTestSuite Suite, Logs Logs) Make(Foundry foundry, ModelPrice? price = null, Prices? prices = null)
    {
        var logs = new Logs();
        var suite = new FoundryModelTestSuite(
            new Factory(foundry), new Credential(),
            new RecipeGenerationOptions(Endpoint, "gpt-4o") { FirstRetryDelay = TimeSpan.Zero },
            new PhotoRecognitionOptions(Endpoint, "gpt-4o") { FirstRetryDelay = TimeSpan.Zero },
            new GenericNameWriterOptions(Endpoint, "gpt-4o", TimeSpan.Zero) { FirstRetryDelay = TimeSpan.Zero },
            new Image(), prices ?? new Prices(price ?? Price), TimeProvider.System, logs);
        return (suite, logs);
    }

    private static Task<SuiteOutcome> Run(FoundryModelTestSuite suite, string deployment = "model-x", List<IReadOnlyList<CheckResult>>? steps = null) =>
        suite.RunAsync(deployment, checks =>
        {
            steps?.Add(checks);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

    private static CheckResult Check(SuiteOutcome outcome, string id) => Assert.Single(outcome.Checks, c => c.Id == id);

    [Fact]
    public async Task A_model_that_does_everything_passes_every_check_stays_usual_and_opens_every_job()
    {
        var foundry = new Foundry();
        var (suite, _) = Make(foundry);

        var outcome = await Run(suite);

        Assert.All(outcome.Checks, c => Assert.True(c.State == CheckStates.Pass, $"{c.Id}: {c.State} {c.Reason}"));
        Assert.Equal(CheckIds.All, outcome.Checks.Select(c => c.Id));
        Assert.Equal(TemperatureMode.Usual, outcome.TemperatureMode);
        Assert.Equal("model-x-2026-01-01", outcome.ModelVersion);
        Assert.Equal(AiJobs.All, ModelTestJobs.Passed(outcome.Checks));
        Assert.All(foundry.Requests, r => Assert.Equal("model-x", r.Deployment)); // this deployment only, no fallback
        Assert.All(outcome.Checks.Where(c => c.Id is not (CheckIds.Cost)), c => Assert.NotNull(c.Ms));
    }

    [Fact]
    public async Task The_real_requests_are_used_a_half_and_half_recipe_without_the_oven_and_slow_cooker_then_the_adjustment()
    {
        var foundry = new Foundry();
        var (suite, _) = Make(foundry);

        await Run(suite);

        var recipe = foundry.Requests.Where(r => (string?)r.Body["response_format"]?["json_schema"]?["name"] == "recipe").ToList();
        Assert.Equal(2, recipe.Count); // the recipe, then the adjustment
        var system = (string)recipe[0].Body["messages"]![0]!["content"]!;
        Assert.Contains("9. Half and half", system, StringComparison.Ordinal);
        Assert.Contains("Never use anything else", system, StringComparison.Ordinal);
        var allowed = recipe[0].Body["response_format"]!["json_schema"]!["schema"]!["properties"]!["equipment"]!["items"]!["enum"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.DoesNotContain("ovenGrill", allowed);
        Assert.DoesNotContain("slowCooker", allowed);
        Assert.Contains("hob", allowed);
        Assert.Contains("chilli flakes", recipe[1].Body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(foundry.Requests, r => (string?)r.Body["response_format"]?["json_schema"]?["name"] == "kitchen_photo"
            && r.Body.ToJsonString().Contains("data:image/png;base64,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lunas_temperature_refusal_sets_default_warns_and_the_rest_passes_without_a_temperature()
    {
        var foundry = new Foundry { RefuseTemperature = true };
        var (suite, _) = Make(foundry);

        var outcome = await Run(suite, "gpt-6-luna");

        var temperature = Check(outcome, CheckIds.Temperature);
        Assert.Equal(CheckStates.Warn, temperature.State);
        Assert.Equal("This model needs its default temperature; we'll leave it out.", temperature.Reason);
        Assert.Equal(TemperatureMode.Default, outcome.TemperatureMode);
        Assert.Equal(AiJobs.All, ModelTestJobs.Passed(outcome.Checks)); // a warning doesn't block
        Assert.All(outcome.Checks.Where(c => c.Id != CheckIds.Temperature), c => Assert.NotEqual(CheckStates.Fail, c.State));
        // Only the one probe that tried our temperature carried one; everything after it, from the real callers too, left it out.
        Assert.Single(foundry.Requests, r => r.Body.ContainsKey("temperature"));
        Assert.True(foundry.Requests.Count > 8);
    }

    [Fact]
    public async Task A_wrong_deployment_name_fails_the_first_check_clearly_and_skips_the_rest()
    {
        var foundry = new Foundry { Missing = true };
        var (suite, _) = Make(foundry);

        var outcome = await Run(suite, "no-such-model");

        var reachable = Check(outcome, CheckIds.Reachable);
        Assert.Equal(CheckStates.Fail, reachable.State);
        Assert.Equal("No deployment with that name on this Foundry account.", reachable.Reason);
        Assert.All(CheckIds.All.Skip(1).Where(id => id is not (CheckIds.Speed or CheckIds.Cost)),
            id => Assert.StartsWith("Skipped:", Check(outcome, id).Reason, StringComparison.Ordinal));
        Assert.Single(foundry.Requests); // nothing else was tried
        Assert.Empty(ModelTestJobs.Passed(outcome.Checks));
    }

    [Fact]
    public async Task A_model_that_refuses_images_fails_only_photos()
    {
        var foundry = new Foundry { RefuseImages = true };
        var (suite, _) = Make(foundry);

        var outcome = await Run(suite);

        var photo = Check(outcome, CheckIds.Photo);
        Assert.Equal(CheckStates.Fail, photo.State);
        Assert.Contains("doesn't accept image input", photo.Reason, StringComparison.Ordinal);
        Assert.Contains("invalid_request_error", photo.Reason, StringComparison.Ordinal);
        Assert.Equal(CheckStates.Pass, Check(outcome, CheckIds.GenericNames).State);
        Assert.Equal([AiJob.Recipes, AiJob.GenericNames], ModelTestJobs.Passed(outcome.Checks));
    }

    [Fact]
    public async Task A_model_that_refuses_strict_schemas_fails_that_check_and_skips_the_real_requests()
    {
        var foundry = new Foundry { RefuseSchemas = true };
        var (suite, _) = Make(foundry);

        var outcome = await Run(suite);

        Assert.Equal(CheckStates.Pass, Check(outcome, CheckIds.Reachable).State);
        var schema = Check(outcome, CheckIds.StrictSchema);
        Assert.Equal(CheckStates.Fail, schema.State);
        Assert.Contains("json_schema is not supported", schema.Reason, StringComparison.Ordinal);
        Assert.StartsWith("Skipped:", Check(outcome, CheckIds.Recipe).Reason, StringComparison.Ordinal);
        Assert.Empty(ModelTestJobs.Passed(outcome.Checks));
    }

    [Fact]
    public async Task Checks_report_progress_waiting_then_running_then_finished_in_order()
    {
        var (suite, _) = Make(new Foundry());
        var steps = new List<IReadOnlyList<CheckResult>>();

        await Run(suite, steps: steps);

        Assert.All(steps, s => Assert.Equal(CheckIds.All, s.Select(c => c.Id)));
        Assert.Equal(CheckStates.Running, steps[0][0].State);
        Assert.All(steps[0].Skip(1), c => Assert.Equal(CheckStates.Waiting, c.State));
        Assert.Equal(CheckStates.Pass, steps[1][0].State); // reachable done before temperature starts
        Assert.Equal(CheckStates.Running, steps[2][1].State);
        Assert.All(steps[^1], c => Assert.Equal(CheckStates.Pass, c.State));
        Assert.Equal(CheckIds.All.Count * 2, steps.Count);
    }

    [Fact]
    public async Task Costs_come_from_the_tokens_each_call_used_and_the_list_price()
    {
        var prices = new Prices(new ModelPrice(2m, 4m, "meters"));
        var (suite, _) = Make(new Foundry(), prices: prices);

        var outcome = await Run(suite);

        // A recipe is one call of 1000 prompt + 100 completion tokens: (1000 × £2 + 100 × £4) / 1M.
        Assert.Equal(0.0024m, outcome.Costs.PerRecipe);
        Assert.Equal(0.0024m, outcome.Costs.PerReadyNow);
        Assert.Equal(0.0024m, outcome.Costs.PerPhoto);
        Assert.True(outcome.Costs.PerRun > outcome.Costs.PerRecipe);
        Assert.Equal("meters", outcome.Costs.Basis);
        Assert.Contains("0.2p a recipe", Check(outcome, CheckIds.Cost).Reason, StringComparison.Ordinal);
        Assert.Equal(["model-x-2026-01-01", "model-x"], Assert.Single(prices.Asked)); // Foundry's version first, then the deployment
        Assert.Equal(1000, Check(outcome, CheckIds.Recipe).PromptTokens);
        Assert.Equal(100, Check(outcome, CheckIds.Recipe).CompletionTokens);
    }

    [Fact]
    public async Task An_unmatched_price_shows_unknown_and_never_fails_the_run()
    {
        var (suite, _) = Make(new Foundry(), prices: new Prices(null));

        var outcome = await Run(suite);

        Assert.Null(outcome.Costs.PerRecipe);
        Assert.Null(outcome.Costs.PerRun);
        var cost = Check(outcome, CheckIds.Cost);
        Assert.Equal(CheckStates.Pass, cost.State);
        Assert.Contains("unknown", cost.Reason, StringComparison.Ordinal);
        Assert.Equal(AiJobs.All, ModelTestJobs.Passed(outcome.Checks));
    }

    [Fact]
    public async Task A_model_that_writes_far_more_output_than_gpt_4o_gets_a_cost_warning_that_doesnt_block()
    {
        var (suite, _) = Make(new Foundry { CompletionTokens = 4000 });

        var outcome = await Run(suite);

        var cost = Check(outcome, CheckIds.Cost);
        Assert.Equal(CheckStates.Warn, cost.State);
        Assert.Contains("hidden reasoning is billed as output", cost.Reason, StringComparison.Ordinal);
        Assert.Equal(AiJobs.All, ModelTestJobs.Passed(outcome.Checks));
    }

    [Fact]
    public async Task Results_and_logs_carry_no_prompt_or_reply_text_and_no_household_data()
    {
        var foundry = new Foundry { RefuseImages = true };
        var (suite, logs) = Make(foundry);

        var outcome = await Run(suite);

        var results = JsonSerializer.Serialize(outcome, ModelTestJson.Options);
        string[] secrets = [RecipeText, "Cook the pasta", "Tomato soup", "Baked beans", "Cheese on toast", "Chicken thighs", "chilli", "base64"];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, results, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(logs.Messages, m => m.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        Assert.NotEmpty(logs.Messages); // the callers did log their numbers
    }

    [Fact]
    public async Task Nothing_runs_without_an_endpoint()
    {
        var logs = new Logs();
        var suite = new FoundryModelTestSuite(
            new Factory(new Foundry()), new Credential(), new RecipeGenerationOptions(null, "gpt-4o"), new PhotoRecognitionOptions(null, "gpt-4o"),
            new GenericNameWriterOptions(null, "gpt-4o", TimeSpan.Zero), new Image(), new Prices(null), TimeProvider.System, logs);

        var outcome = await Run(suite);

        Assert.All(outcome.Checks, c => Assert.Equal(CheckStates.Fail, c.State));
        Assert.Empty(ModelTestJobs.Passed(outcome.Checks));
    }

    // ---- Speed (check 9)

    private static AiCall Call(int ms, string outcome = AiCall.Ok) => new("d", ms, 1, 1, 200, null, null, null, outcome);

    [Fact]
    public void Speed_passes_under_20_seconds_warns_over_it_and_fails_at_the_limit()
    {
        Assert.Equal(CheckStates.Pass, FoundryModelTestSuite.Speed([Call(3_000), Call(19_999)]).State);
        var slow = FoundryModelTestSuite.Speed([Call(3_000), Call(31_200)]);
        Assert.Equal(CheckStates.Warn, slow.State);
        Assert.Equal("The slowest call took 31.2 s.", slow.Reason);
        Assert.Equal(31_200, slow.Ms);
        var limit = FoundryModelTestSuite.Speed([Call(45_000, AiCall.Timeout), Call(2_000)]);
        Assert.Equal(CheckStates.Fail, limit.State);
        Assert.Equal("1 call hit the 45 s limit.", limit.Reason);
    }

    [Fact]
    public void A_refused_temperature_is_recognised_by_code_or_message_but_only_on_a_400()
    {
        AiCall Refusal(int status, string? code, string? message) => new("d", 1, 0, 0, status, code, message, null, AiCall.Refused);

        Assert.True(FoundryModelTestSuite.IsTemperatureRefusal(Refusal(400, "unsupported_value", "x")));
        Assert.True(FoundryModelTestSuite.IsTemperatureRefusal(Refusal(400, "invalid_request_error", "'temperature' is not supported")));
        Assert.False(FoundryModelTestSuite.IsTemperatureRefusal(Refusal(404, "DeploymentNotFound", "no")));
        Assert.False(FoundryModelTestSuite.IsTemperatureRefusal(Refusal(400, "invalid_request_error", "bad json")));
    }

    [Fact]
    public void Costs_read_as_pence_under_a_pound_and_unknown_when_missing()
    {
        Assert.Equal("2.4p", FoundryModelTestSuite.Pence(0.0240m));
        Assert.Equal("0.2p", FoundryModelTestSuite.Pence(0.0024m));
        Assert.Equal("£1.50", FoundryModelTestSuite.Pence(1.5m));
        Assert.Equal("unknown", FoundryModelTestSuite.Pence(null));
    }

    // ---- The test image

    [Fact]
    public void The_pack_image_is_a_small_valid_png_with_no_repo_content()
    {
        var png = TestPackImage.Create();

        Assert.True(png.Length is > 500 and < 50 * 1024, $"{png.Length} bytes");
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal(TestPackImage.Width, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(TestPackImage.Height, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal("image/png", FoundryPhotoRecogniser.MediaType(png));
        Assert.Equal("image/jpeg", FoundryPhotoRecogniser.MediaType([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(png, TestPackImage.Create()); // the same picture every time
    }

    [Fact]
    public async Task The_image_is_made_when_missing_and_reused_afterwards()
    {
        byte[]? stored = null;
        var created = 0;
        Task<byte[]?> Read() => Task.FromResult(stored);
        Task<byte[]?> Create(byte[] image)
        {
            created++;
            stored = image;
            return Task.FromResult<byte[]?>(image);
        }

        var first = await TestImageSource.GetOrCreateAsync(Read, Create, Read);
        stored![0] = 0x01; // Gary replaced the blob with his own picture
        var second = await TestImageSource.GetOrCreateAsync(Read, Create, Read);

        Assert.True(first.Length < 50 * 1024);
        Assert.Equal(1, created);
        Assert.Equal(0x01, second[0]);
    }

    [Fact]
    public async Task Losing_the_race_to_make_the_image_uses_the_winners()
    {
        byte[]? stored = null;
        var winner = new byte[] { 1, 2, 3 };

        var image = await TestImageSource.GetOrCreateAsync(
            () => Task.FromResult(stored),
            _ =>
            {
                stored = winner; // someone else uploaded first
                return Task.FromResult<byte[]?>(null);
            },
            () => Task.FromResult(stored));

        Assert.Equal(winner, image);
    }

    [Fact]
    public void The_canned_stock_has_twelve_made_up_items_and_no_household_data()
    {
        Assert.Equal(12, CannedStock.Items.Count);
        Assert.Equal(12, CannedStock.Items.Select(i => i.ProductId).Distinct().Count());
        Assert.All(CannedStock.Items, i => Assert.StartsWith("00000000-0000-4000-8000-", i.ProductId.ToString(), StringComparison.Ordinal));
        Assert.Contains(CannedStock.Items, i => i.AnimalContent == Domain.Products.AnimalContent.Meat);
        var request = CannedStock.HalfAndHalf;
        Assert.Equal(Application.Recipes.Diet.Half, request.Diet);
        Assert.DoesNotContain(Application.Recipes.Kit.OvenGrill, request.AllowedKit);
        Assert.DoesNotContain(Application.Recipes.Kit.SlowCooker, request.AllowedKit);
        Assert.Contains(Application.Recipes.Kit.Hob, request.AllowedKit);
    }
}
