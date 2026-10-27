using System.Net;
using CupboardTracker.Application.AiModels;
using CupboardTracker.Functions.Api.Settings;

namespace CupboardTracker.Functions.Api.Tests;

// The model test suite's endpoints (#121): the status each outcome maps to, and what the body carries.
public sealed class ModelTestFunctionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static ModelTestRun Run() => ModelTestRun.Queued(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        new AiModel("luna", "Luna", "gpt-6-luna", TemperatureMode.Usual, [], Guid.NewGuid(), "Gary", Now),
        new Application.Users.CurrentUser(Guid.NewGuid(), "Gary"), Now);

    [Fact]
    public void A_new_run_is_202_with_every_check_waiting_and_a_run_id_to_poll()
    {
        var (status, body) = ModelTestFunctions.ToResponse(new StartModelTestResult.Started(Run()));

        Assert.Equal(HttpStatusCode.Accepted, status);
        var json = Assert.IsAssignableFrom<System.Text.Json.Nodes.JsonObject>(body);
        Assert.Equal("11111111-2222-3333-4444-555555555555", (string?)json["runId"]);
        Assert.Equal("queued", (string?)json["status"]);
        Assert.Equal(10, json["checks"]!.AsArray().Count);
        Assert.All(json["checks"]!.AsArray(), c => Assert.Equal("waiting", (string?)c!["state"]));
        Assert.Equal("2026-10-27T09:00:00.000Z", (string?)json["startedAt"]);
    }

    [Fact]
    public void A_run_already_going_is_200_the_same_shape()
    {
        var (status, body) = ModelTestFunctions.ToResponse(new StartModelTestResult.AlreadyRunning(Run()));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("queued", (string?)((System.Text.Json.Nodes.JsonObject)body)["status"]);
    }

    [Fact]
    public void An_unknown_model_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, ModelTestFunctions.ToResponse(new StartModelTestResult.NotFound()).Status);
    }

    [Fact]
    public void The_sixth_run_is_429_with_when_to_try_again()
    {
        var (status, body) = ModelTestFunctions.ToResponse(new StartModelTestResult.TooMany(Now.AddMinutes(40)));

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        Assert.Contains("\"retryAt\":\"2026-10-27T09:40:00.000Z\"", json, StringComparison.Ordinal);
        Assert.Contains("five test runs", json, StringComparison.Ordinal);
    }
}
