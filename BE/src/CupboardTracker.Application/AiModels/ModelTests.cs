using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.AiModels;

/// <summary>The suite's checks, in the order they run (#121). Ids are what the API returns.</summary>
public static class CheckIds
{
    public const string Reachable = "reachable";
    public const string Temperature = "temperature";
    public const string StrictSchema = "strictSchema";
    public const string Recipe = "recipe";
    public const string ReadyNow = "readyNow";
    public const string Adjust = "adjust";
    public const string Photo = "photo";
    public const string GenericNames = "genericNames";
    public const string Speed = "speed";
    public const string Cost = "cost";

    public static readonly IReadOnlyList<string> All =
        [Reachable, Temperature, StrictSchema, Recipe, ReadyNow, Adjust, Photo, GenericNames, Speed, Cost];
}

public static class CheckStates
{
    public const string Waiting = "waiting";
    public const string Running = "running";
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";
}

public static class RunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";

    public static bool IsOpen(string status) => status is Queued or Running;
}

/// <summary>One check's outcome. <see cref="Reason"/> is one line: Foundry's own code and message on a refusal, never the
/// prompt or reply. Tokens and time are numbers only.</summary>
public sealed record CheckResult(
    string Id, string State, string? Reason = null, int? Ms = null, int? PromptTokens = null, int? CompletionTokens = null)
{
    public static CheckResult Waiting(string id) => new(id, CheckStates.Waiting);
}

/// <summary>Estimated cost in pounds of one recipe, one Ready now set and one photo, from the tokens the run used and the
/// public list price. Null means unknown (no price matched). <see cref="Basis"/> says which price was used.</summary>
public sealed record RunCosts(decimal? PerRecipe, decimal? PerReadyNow, decimal? PerPhoto, decimal? PerRun, string? Basis);

/// <summary>A run of the suite on one registered model, as saved and polled (#121).</summary>
public sealed record ModelTestRun(
    Guid RunId,
    string ModelId,
    string Deployment,
    Guid StartedBy,
    string StartedByName,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    IReadOnlyList<CheckResult> Checks,
    IReadOnlyList<string> Jobs,
    string TemperatureMode,
    string? ModelVersion,
    RunCosts? Costs,
    int? TotalMs,
    string? Error = null)
{
    public static ModelTestRun Queued(Guid runId, AiModel model, CurrentUser user, DateTimeOffset now) => new(
        runId, model.Id, model.Deployment, user.ObjectId, user.DisplayName, now, null, RunStatuses.Queued,
        [.. CheckIds.All.Select(CheckResult.Waiting)], [], TemperatureModes.Id(model.TemperatureMode), null, null, null);
}

/// <summary>What the suite found: every check, the temperature mode it settled on, the model version Foundry reports.</summary>
public sealed record SuiteOutcome(
    IReadOnlyList<CheckResult> Checks, TemperatureMode TemperatureMode, string? ModelVersion, RunCosts Costs, int TotalMs);

/// <summary>Runs the checks against one deployment. <paramref name="progress"/> gets all the checks (waiting, running or
/// finished) after each change, so a poll sees results as they complete. Implemented in Infrastructure.</summary>
public interface IModelTestSuite
{
    Task<SuiteOutcome> RunAsync(string deployment, Func<IReadOnlyList<CheckResult>, Task> progress, CancellationToken cancellationToken);
}

/// <summary>Run documents are short-lived (a day): the latest results also live on the model itself.</summary>
public interface IModelTestRunRepository
{
    Task CreateAsync(ModelTestRun run, CancellationToken cancellationToken);

    Task<Versioned<ModelTestRun>?> GetAsync(Guid runId, CancellationToken cancellationToken);

    Task<bool> TryReplaceAsync(Versioned<ModelTestRun> current, ModelTestRun updated, CancellationToken cancellationToken);

    Task SaveAsync(ModelTestRun run, CancellationToken cancellationToken);

    /// <summary>The run going on for this model, if one is marked (the mark expires on its own).</summary>
    Task<Guid?> GetActiveRunIdAsync(string modelId, CancellationToken cancellationToken);

    /// <summary>Marks <paramref name="runId"/> as the model's run; false if another is already marked.</summary>
    Task<bool> TryMarkActiveAsync(string modelId, Guid runId, CancellationToken cancellationToken);

    Task ClearActiveAsync(string modelId, CancellationToken cancellationToken);
}

public interface IModelTestScheduler
{
    Task EnqueueAsync(Guid runId, CancellationToken cancellationToken);
}

/// <summary>Which jobs a run's checks open up (#121): a job's model must pass every check that job needs. Warnings (slow,
/// costly, "needs its default temperature") don't block; a skipped check counts as a fail.</summary>
public static class ModelTestJobs
{
    public static IReadOnlyList<string> Needs(AiJob job) => job switch
    {
        AiJob.Recipes => [CheckIds.Reachable, CheckIds.Temperature, CheckIds.StrictSchema, CheckIds.Recipe, CheckIds.ReadyNow, CheckIds.Adjust, CheckIds.Speed],
        AiJob.Photos => [CheckIds.Reachable, CheckIds.Temperature, CheckIds.StrictSchema, CheckIds.Photo, CheckIds.Speed],
        _ => [CheckIds.Reachable, CheckIds.Temperature, CheckIds.StrictSchema, CheckIds.GenericNames, CheckIds.Speed],
    };

    public static IReadOnlyList<AiJob> Passed(IReadOnlyList<CheckResult> checks) =>
        [.. AiJobs.All.Where(job => Needs(job).All(id => checks.FirstOrDefault(c => c.Id == id) is { State: CheckStates.Pass or CheckStates.Warn }))];
}

/// <summary>How runs become JSON, for the API and for the copy stored on the model.</summary>
public static class ModelTestJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonObject ToNode(ModelTestRun run) => (JsonObject)JsonSerializer.SerializeToNode(run, Options)!;

    public static ModelTestRun? FromNode(JsonNode? node) => node is null ? null : node.Deserialize<ModelTestRun>(Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        options.Converters.Add(new UtcTimestampConverter());
        return options;
    }

    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }
}

public abstract record StartModelTestResult
{
    /// <summary>202: queued. <see cref="Run"/> is the new run.</summary>
    public sealed record Started(ModelTestRun Run) : StartModelTestResult;

    /// <summary>200: this model already has a run going, so that one is returned and nothing new is queued.</summary>
    public sealed record AlreadyRunning(ModelTestRun Run) : StartModelTestResult;

    public sealed record NotFound : StartModelTestResult;

    /// <summary>429: this person has started their runs for the hour.</summary>
    public sealed record TooMany(DateTimeOffset RetryAt) : StartModelTestResult;
}

/// <summary>Limits on runs (#121): at most this many a person starts an hour.</summary>
public sealed record ModelTestLimits(int PerHour = 5)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>A run that hasn't finished after this long is treated as abandoned, so a new one can start.</summary>
    public static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(15);
}

/// <summary><c>POST /api/settings/ai-models/models/{id}/test</c> (#121): queues a run, or returns the one already going for
/// that model. Five a person an hour (kept in memory on the instance, like the test push's limit).</summary>
public sealed class StartModelTest(
    IAiModelSettingsRepository settings, IModelTestRunRepository runs, IModelTestScheduler queue, ModelTestLimits limits, TimeProvider clock)
{
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> started = [];
    private readonly Lock gate = new();

    public async Task<StartModelTestResult> ExecuteAsync(string modelId, CurrentUser user, CancellationToken cancellationToken)
    {
        var model = (await settings.GetAsync(cancellationToken))?.Value.Find(modelId);
        if (model is null)
        {
            return new StartModelTestResult.NotFound();
        }

        if (await OpenRunAsync(modelId, cancellationToken) is { } open)
        {
            return new StartModelTestResult.AlreadyRunning(open);
        }

        if (TryTake(user.ObjectId) is { } retryAt)
        {
            return new StartModelTestResult.TooMany(retryAt);
        }

        var run = ModelTestRun.Queued(Guid.NewGuid(), model, user, clock.GetUtcNow());
        await runs.CreateAsync(run, cancellationToken);
        if (!await runs.TryMarkActiveAsync(modelId, run.RunId, cancellationToken))
        {
            // Someone started one a moment ago: theirs is the run.
            if (await OpenRunAsync(modelId, cancellationToken) is { } other)
            {
                return new StartModelTestResult.AlreadyRunning(other);
            }
        }

        await queue.EnqueueAsync(run.RunId, cancellationToken);
        return new StartModelTestResult.Started(run);
    }

    /// <summary>How many more runs this person can start this hour (the UI says so before they start one).</summary>
    public int RunsLeft(Guid userId)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            var recent = started.TryGetValue(userId, out var times) ? times.Count(t => now - t < ModelTestLimits.Window) : 0;
            return Math.Max(0, limits.PerHour - recent);
        }
    }

    /// <summary>The model's run that is still going, or null (clearing a mark whose run finished, vanished or was abandoned).</summary>
    private async Task<ModelTestRun?> OpenRunAsync(string modelId, CancellationToken cancellationToken)
    {
        if (await runs.GetActiveRunIdAsync(modelId, cancellationToken) is not { } runId)
        {
            return null;
        }

        var run = (await runs.GetAsync(runId, cancellationToken))?.Value;
        if (run is not null && RunStatuses.IsOpen(run.Status) && clock.GetUtcNow() - run.StartedAt < ModelTestLimits.Abandoned)
        {
            return run;
        }

        await runs.ClearActiveAsync(modelId, cancellationToken);
        return null;
    }

    /// <summary>Null when taken; otherwise when the next run is allowed.</summary>
    private DateTimeOffset? TryTake(Guid userId)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (!started.TryGetValue(userId, out var times))
            {
                started[userId] = times = new Queue<DateTimeOffset>();
            }

            while (times.Count > 0 && now - times.Peek() >= ModelTestLimits.Window)
            {
                times.Dequeue();
            }

            if (times.Count >= limits.PerHour)
            {
                return times.Peek() + ModelTestLimits.Window;
            }

            times.Enqueue(now);
            return null;
        }
    }
}

/// <summary>Which models have a run going (#123), so a model's card can say "Testing…" after the person left the page.
/// Read-only: a stale mark (finished, vanished or abandoned) just isn't reported.</summary>
public sealed class OpenModelTestRuns(IModelTestRunRepository runs, TimeProvider clock)
{
    public async Task<IReadOnlyDictionary<string, Guid>> ForAsync(IEnumerable<string> modelIds, CancellationToken cancellationToken)
    {
        var open = new Dictionary<string, Guid>();
        foreach (var modelId in modelIds)
        {
            if (await runs.GetActiveRunIdAsync(modelId, cancellationToken) is { } runId
                && (await runs.GetAsync(runId, cancellationToken))?.Value is { } run
                && RunStatuses.IsOpen(run.Status) && clock.GetUtcNow() - run.StartedAt < ModelTestLimits.Abandoned)
            {
                open[modelId] = runId;
            }
        }

        return open;
    }
}

/// <summary><c>GET /api/settings/ai-models/test-runs/{runId}</c> (#121): the run so far.</summary>
public sealed class GetModelTestRun(IModelTestRunRepository runs)
{
    public async Task<ModelTestRun?> ExecuteAsync(Guid runId, CancellationToken cancellationToken) =>
        (await runs.GetAsync(runId, cancellationToken))?.Value;
}

/// <summary>
/// Runs a queued run (#121), from the queue-triggered function: claims it (a message delivered twice runs once), runs the
/// suite while saving each check as it finishes, then saves the outcome on the model: its jobs, its temperature mode and
/// the run itself. A crash is saved as a failed run, never retried into a duplicate.
/// </summary>
public sealed partial class RunModelTest(
    IModelTestRunRepository runs, IModelTestSuite suite, ManageAiModels models, TimeProvider clock, ILogger<RunModelTest> logger)
{
    public async Task ExecuteAsync(Guid runId, CancellationToken cancellationToken)
    {
        if (await runs.GetAsync(runId, cancellationToken) is not { } queued || queued.Value.Status != RunStatuses.Queued)
        {
            return; // unknown, or already claimed or finished
        }

        var run = queued.Value with { Status = RunStatuses.Running };
        if (!await runs.TryReplaceAsync(queued, run, cancellationToken))
        {
            return; // another delivery claimed it
        }

        try
        {
            var outcome = await suite.RunAsync(run.Deployment, async checks =>
            {
                run = run with { Checks = checks };
                await runs.SaveAsync(run, cancellationToken);
            }, cancellationToken);

            var jobs = ModelTestJobs.Passed(outcome.Checks);
            run = run with
            {
                Status = RunStatuses.Done,
                FinishedAt = clock.GetUtcNow(),
                Checks = outcome.Checks,
                Jobs = [.. jobs.Select(AiJobs.Id)],
                TemperatureMode = TemperatureModes.Id(outcome.TemperatureMode),
                ModelVersion = outcome.ModelVersion,
                Costs = outcome.Costs,
                TotalMs = outcome.TotalMs,
            };
            var recorded = await models.RecordTestsAsync(run.ModelId, jobs, outcome.TemperatureMode, ModelTestJson.ToNode(run), cancellationToken);
            await runs.SaveAsync(run, cancellationToken);
            LogDone(runId, run.Deployment, string.Join(',', run.Jobs), run.TemperatureMode, recorded);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(e, runId, run.Deployment);
            run = run with { Status = RunStatuses.Failed, FinishedAt = clock.GetUtcNow(), Error = "The run stopped unexpectedly." };
            await runs.SaveAsync(run, CancellationToken.None);
        }
        finally
        {
            await runs.ClearActiveAsync(run.ModelId, CancellationToken.None);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Model test {RunId} on {Deployment} done: jobs [{Jobs}], temperature {TemperatureMode}, saved on the model: {Recorded}.")]
    private partial void LogDone(Guid runId, string deployment, string jobs, string temperatureMode, bool recorded);

    [LoggerMessage(Level = LogLevel.Error, Message = "Model test {RunId} on {Deployment} stopped unexpectedly.")]
    private partial void LogFailed(Exception exception, Guid runId, string deployment);
}
