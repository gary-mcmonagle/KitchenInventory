using CupboardTracker.Application.AiModels;
using CupboardTracker.Infrastructure.Cosmos;

namespace CupboardTracker.Infrastructure.Tests;

// Model test runs in pushSeen (#121): short-lived documents keyed by run, holding the run as the API returns it.
public sealed class ModelTestRunDocumentTests
{
    [Fact]
    public void A_run_document_has_its_key_a_ttl_and_the_run_and_reads_back()
    {
        var now = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        var run = ModelTestRun.Queued(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            new AiModel("luna", "Luna", "gpt-6-luna", TemperatureMode.Default, [], Guid.NewGuid(), "Gary", now),
            new Application.Users.CurrentUser(Guid.NewGuid(), "Gary"), now);

        var document = CosmosModelTestRunRepository.ToDocument(run);

        Assert.Equal("testrun:11111111-2222-3333-4444-555555555555", (string?)document["id"]);
        Assert.Equal((string?)document["id"], (string?)document["eventKey"]); // id = partition key
        Assert.Equal(24 * 3600, (int?)document["ttl"]);
        Assert.Equal("modelTestRun", (string?)document["type"]);
        var back = ModelTestJson.FromNode(document)!;
        Assert.Equal(run with { Checks = [], Jobs = [] }, back with { Checks = [], Jobs = [] });
        Assert.Equal(run.Checks, back.Checks);
        Assert.Equal("testrun-active:luna", CosmosModelTestRunRepository.ActiveKey("luna"));
    }
}
