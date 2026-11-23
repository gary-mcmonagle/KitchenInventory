using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application.Usuals;
using CupboardTracker.Infrastructure.Cosmos;
using CupboardTracker.Infrastructure.Jev;
using CupboardTracker.Infrastructure.Usuals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CupboardTracker.Infrastructure.Tests;

public sealed class UsualsTests
{
    private const string Key = "test-key-not-a-real-one";

    private static readonly JevOptions Options = new(new Uri("https://jev.example.test"), "jev-1.13.0", Key) { FirstRetryDelay = TimeSpan.Zero };

    private static UsualsCandidate Milk(params bool[] weeks) =>
        new("milk", "Semi-skimmed milk", "dairyEggs", Guid.NewGuid(), null, weeks.Length == 0 ? [true, true, false, true] : weeks);

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Bodies)
            {
                Bodies.Add(body);
            }

            return respond(request, body);
        }
    }

    private static HttpResponseMessage Answer(double p, int tokens = 120) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"model\":\"jev-1.13.0\",\"answers\":{\"due\":{\"type\":\"noul\",\"noul\":"
            + p.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"confidence\":0.5}},\"usage\":{\"input_tokens\":" + tokens + ",\"output_tokens\":0}}",
            System.Text.Encoding.UTF8, "application/json"),
    };

    private static JevUsualsJudge Judge(Handler handler, JevOptions? options = null, UsualsSettings? settings = null) =>
        new(new JevClient(new HttpClient(handler), options ?? Options, TimeProvider.System), options ?? Options, settings ?? new UsualsSettings(),
            NullLogger<JevUsualsJudge>.Instance);

    [Fact]
    public async Task Asks_one_yes_no_question_per_candidate_each_in_its_own_request_and_returns_the_probabilities_in_order()
    {
        var handler = new Handler((_, body) => Answer(body.Contains("Rice") ? 0.3 : 0.9));
        var candidates = new[] { Milk(), new UsualsCandidate("rice", "Rice", "pastaRiceGrains", Guid.NewGuid(), null, [true, false, true]), Milk(true, true) };

        var result = Assert.IsType<UsualsJudgement.Judged>(await Judge(handler).JudgeAsync(candidates, CancellationToken.None));

        Assert.Equal([0.9, 0.3, 0.9], result.Probabilities);
        Assert.Equal(360, result.InputTokens);
        Assert.Equal(3, handler.Bodies.Count);
        foreach (var body in handler.Bodies)
        {
            var request = JsonNode.Parse(body)!;
            Assert.Equal("jev-1.13.0", (string?)request["model"]);
            Assert.Equal("noul", (string?)request["questions"]!["due"]!["type"]);
            Assert.Equal(JevUsualsJudge.Instruction, (string?)request["questions"]!["due"]!["instructions"]);
        }
    }

    [Fact]
    public void The_state_is_the_item_its_aisle_and_one_sentence_of_weekly_history_and_nothing_else()
    {
        var state = JevUsualsJudge.State(new UsualsCandidate("eggs", "Medium eggs", "dairyEggs", Guid.NewGuid(), "g1", [true, true, false, true]));

        Assert.Equal(["item", "aisle", "history"], state.Select(p => p.Key));
        Assert.Equal("Medium eggs", (string?)state["item"]);
        Assert.Equal("dairy and eggs", (string?)state["aisle"]);
        Assert.Equal(
            "The household does one big shop a week. In the last 4 weekly shops (oldest first), was this bought that week? "
            + "yes, yes, no, yes. Bought in 3 of 4 weeks; bought in the most recent shop.",
            (string?)state["history"]);
        var older = JevUsualsJudge.State(new UsualsCandidate("a", "A", "other", Guid.NewGuid(), null, [true, true, false, false]));
        Assert.EndsWith("Bought in 2 of 4 weeks; last bought 2 shops ago.", (string?)older["history"]);
    }

    [Fact]
    public async Task The_key_goes_only_in_the_header_and_the_request_holds_no_stock_ids_or_people()
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal("Bearer " + Key, request.Headers.Authorization!.ToString());
            return Answer(0.8);
        });
        var candidate = Milk();

        await Judge(handler).JudgeAsync([candidate], CancellationToken.None);

        var body = Assert.Single(handler.Bodies);
        Assert.DoesNotContain(Key, body);
        Assert.DoesNotContain(candidate.ProductId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stock", body.Replace(JevUsualsJudge.Instruction, ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Is_unavailable_when_not_configured_without_calling_Jev()
    {
        var handler = new Handler((_, _) => Answer(1));
        var options = Options with { ApiKey = null };

        var result = Assert.IsType<UsualsJudgement.Unavailable>(await Judge(handler, options).JudgeAsync([Milk()], CancellationToken.None));

        Assert.Equal("not configured", result.Reason);
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(422)]
    public async Task One_unanswered_question_makes_the_whole_judgement_unavailable(int status)
    {
        var handler = new Handler((_, body) => body.Contains("Rice") ? new HttpResponseMessage((HttpStatusCode)status) : Answer(0.9));
        var candidates = new[] { Milk(), new UsualsCandidate("rice", "Rice", "other", Guid.NewGuid(), null, [true, true]) };

        var result = Assert.IsType<UsualsJudgement.Unavailable>(await Judge(handler).JudgeAsync(candidates, CancellationToken.None));

        Assert.Equal("1 of 2 not answered", result.Reason);
    }

    [Fact]
    public async Task An_answer_outside_zero_to_one_or_missing_counts_as_unanswered()
    {
        var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"answers":{"due":{"type":"noul","noul":1.7}}}""", System.Text.Encoding.UTF8, "application/json"),
        });

        Assert.IsType<UsualsJudgement.Unavailable>(await Judge(handler).JudgeAsync([Milk()], CancellationToken.None));
    }

    [Fact]
    public void A_job_document_has_the_container_keys_a_ttl_and_reads_back()
    {
        var job = new UsualsJob(Guid.NewGuid(), Guid.NewGuid(), UsualsStatus.Ready, UsualsSteps.List, UsualsMethods.Jev,
            [new UsualsItem("Medium eggs", "g1", "dairyEggs", UsualsBand.Maybe, "Bought in 3 of the last 4 weeks", Guid.NewGuid())],
            new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));

        var document = CosmosUsualsJobRepository.ToDocument(job);

        Assert.Equal($"usuals:{job.JobId}", (string?)document["id"]);
        Assert.Equal((string?)document["id"], (string?)document["eventKey"]);
        Assert.True((int)document["ttl"]! > 0);
        var read = Assert.IsType<UsualsJob>(CosmosUsualsJobRepository.FromDocument(document));
        Assert.Equal(job, read with { Items = job.Items }); // records compare lists by reference
        Assert.Equal(job.Items, read.Items);
    }

    [Fact]
    public void A_job_document_that_is_unreadable_is_no_job()
    {
        Assert.Null(CosmosUsualsJobRepository.FromDocument(new JsonObject { ["jobId"] = "not a guid" }));
    }

    [Fact]
    public void The_queue_message_carries_the_job_id_and_a_bad_one_reads_as_nothing()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, StorageQueueUsualsScheduler.Deserialize(StorageQueueUsualsScheduler.Serialize(id)));
        Assert.Null(StorageQueueUsualsScheduler.Deserialize("""{"jobId":"00000000-0000-0000-0000-000000000000"}"""));
        Assert.Equal("usuals-jobs", StorageQueueUsualsScheduler.QueueName);
    }

    [Fact]
    public void The_settings_default_to_the_spike_and_can_be_set_from_configuration()
    {
        static IConfiguration Config(params (string, string)[] pairs) =>
            new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Item1, p.Item2))).Build();

        var defaults = ServiceCollectionExtensions.UsualsSettingsFromConfiguration(Config());
        Assert.Equal((0.8, 0.6, 0.75, 0.5, 10, TimeSpan.FromSeconds(60)), (defaults.DueConfidence, defaults.MaybeConfidence, defaults.PatternDue, defaults.PatternMaybe, defaults.RequestsPerHour, defaults.Budget));

        var custom = ServiceCollectionExtensions.UsualsSettingsFromConfiguration(Config(
            ("USUALS_JEV_DUE_CONFIDENCE", "0.9"), ("USUALS_JEV_MAYBE_CONFIDENCE", "0.7"), ("USUALS_REQUESTS_PER_HOUR", "3"), ("USUALS_JOB_BUDGET_SECONDS", "30")));
        Assert.Equal((0.9, 0.7, 3, TimeSpan.FromSeconds(30)), (custom.DueConfidence, custom.MaybeConfidence, custom.RequestsPerHour, custom.Budget));

        Assert.Throws<InvalidOperationException>(() => ServiceCollectionExtensions.UsualsSettingsFromConfiguration(Config(("USUALS_JEV_DUE_CONFIDENCE", "1.5"))));
        Assert.Throws<InvalidOperationException>(() => ServiceCollectionExtensions.UsualsSettingsFromConfiguration(
            Config(("USUALS_JEV_DUE_CONFIDENCE", "0.5"), ("USUALS_JEV_MAYBE_CONFIDENCE", "0.6"))));
        Assert.Throws<InvalidOperationException>(() => ServiceCollectionExtensions.UsualsSettingsFromConfiguration(Config(("USUALS_REQUESTS_PER_HOUR", "0"))));
    }
}
