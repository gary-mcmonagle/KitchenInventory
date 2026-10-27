using System.Globalization;
using System.Net;
using System.Text.Json;
using CupboardTracker.Application.AiModels;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Infrastructure.ModelTesting;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Functions.Api.Settings;

/// <summary>The model test suite (#121). All need a signed-in user. See docs/api.md.</summary>
public sealed class ModelTestFunctions(StartModelTest start, GetModelTestRun get)
{
    /// <summary><c>POST /api/settings/ai-models/models/{id}/test</c>: queues a run and answers 202 with the run (its
    /// <c>runId</c> to poll). A run already going for that model is returned instead (200). 404 for no such model, 429
    /// (with <c>retryAt</c>) after five starts an hour.</summary>
    [Function("startModelTest")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settings/ai-models/models/{id}/test")] HttpRequestData request,
        string id, FunctionContext context, CancellationToken cancellationToken)
    {
        var (status, body) = ToResponse(await start.ExecuteAsync(id, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>GET /api/settings/ai-models/test-runs/{runId}</c>: the run so far.</summary>
    [Function("getModelTestRun")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/ai-models/test-runs/{runId}")] HttpRequestData request,
        string runId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(runId, out var id) || await get.ExecuteAsync(id, cancellationToken) is not { } run)
        {
            return await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such test run." });
        }

        var response = await JsonAsync(request, HttpStatusCode.OK, ModelTestJson.ToNode(run));
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(StartModelTestResult result) => result switch
    {
        StartModelTestResult.Started s => (HttpStatusCode.Accepted, ModelTestJson.ToNode(s.Run)),
        StartModelTestResult.AlreadyRunning a => (HttpStatusCode.OK, ModelTestJson.ToNode(a.Run)),
        StartModelTestResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such model." }),
        StartModelTestResult.TooMany t => (HttpStatusCode.TooManyRequests, new
        {
            error = "You've started five test runs this hour. Try again later.",
            retryAt = t.RetryAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData request, HttpStatusCode status, object body)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, ModelTestJson.Options));
        return response;
    }
}

/// <summary>Runs a queued model test (#121). The run is claimed first, so a message delivered twice runs it once; a bad
/// message is logged and dropped.</summary>
public sealed partial class ModelTestRunFunction(RunModelTest run, ILogger<ModelTestRunFunction> logger)
{
    [Function("modelTestRun")]
    public async Task Run(
        [QueueTrigger(StorageQueueModelTestScheduler.QueueName, Connection = "AzureWebJobsStorage")] string message,
        CancellationToken cancellationToken)
    {
        Guid? runId;
        try
        {
            runId = StorageQueueModelTestScheduler.Deserialize(message);
        }
        catch (JsonException)
        {
            runId = null;
        }

        if (runId is { } id)
        {
            await run.ExecuteAsync(id, cancellationToken);
        }
        else
        {
            LogUnreadable();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A model test message couldn't be read; dropped.")]
    private partial void LogUnreadable();
}
