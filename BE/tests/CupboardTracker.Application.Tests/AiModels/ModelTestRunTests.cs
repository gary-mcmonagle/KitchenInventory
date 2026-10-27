using CupboardTracker.Application.AiModels;
using CupboardTracker.Application.Users;

namespace CupboardTracker.Application.Tests.AiModels;

internal sealed class FakeRunStore : IModelTestRunRepository
{
    private readonly Dictionary<Guid, (ModelTestRun Run, int Version)> runs = [];
    private readonly Dictionary<string, Guid> marks = [];

    public List<ModelTestRun> Saves { get; } = [];

    public ModelTestRun Get(Guid id) => runs[id].Run;

    public int MarkCount => marks.Count;

    public Task CreateAsync(ModelTestRun run, CancellationToken cancellationToken)
    {
        runs[run.RunId] = (run, 1);
        return Task.CompletedTask;
    }

    public Task<Versioned<ModelTestRun>?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        Task.FromResult(runs.TryGetValue(runId, out var r) ? new Versioned<ModelTestRun>(r.Run, $"v{r.Version}") : null);

    public Task<bool> TryReplaceAsync(Versioned<ModelTestRun> current, ModelTestRun updated, CancellationToken cancellationToken)
    {
        if (!runs.TryGetValue(updated.RunId, out var r) || current.ETag != $"v{r.Version}")
        {
            return Task.FromResult(false);
        }

        runs[updated.RunId] = (updated, r.Version + 1);
        return Task.FromResult(true);
    }

    public Task SaveAsync(ModelTestRun run, CancellationToken cancellationToken)
    {
        runs[run.RunId] = (run, runs.TryGetValue(run.RunId, out var r) ? r.Version + 1 : 1);
        Saves.Add(run);
        return Task.CompletedTask;
    }

    public Task<Guid?> GetActiveRunIdAsync(string modelId, CancellationToken cancellationToken) =>
        Task.FromResult(marks.TryGetValue(modelId, out var id) ? (Guid?)id : null);

    public Task<bool> TryMarkActiveAsync(string modelId, Guid runId, CancellationToken cancellationToken)
    {
        if (marks.ContainsKey(modelId))
        {
            return Task.FromResult(false);
        }

        marks[modelId] = runId;
        return Task.FromResult(true);
    }

    public Task ClearActiveAsync(string modelId, CancellationToken cancellationToken)
    {
        marks.Remove(modelId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeScheduler : IModelTestScheduler
{
    public List<Guid> Queued { get; } = [];

    public Task EnqueueAsync(Guid runId, CancellationToken cancellationToken)
    {
        Queued.Add(runId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSuite : IModelTestSuite
{
    public SuiteOutcome? Outcome { get; set; }

    public Exception? Throw { get; set; }

    public int Runs { get; private set; }

    public List<IReadOnlyList<CheckResult>> Steps { get; } = [];

    public async Task<SuiteOutcome> RunAsync(string deployment, Func<IReadOnlyList<CheckResult>, Task> progress, CancellationToken cancellationToken)
    {
        Runs++;
        foreach (var step in Steps)
        {
            await progress(step);
        }

        return Throw is not null ? throw Throw : Outcome!;
    }
}

// The model test suite's runs (#121): starting (async, deduplicated, limited), running (claimed once, saved per check, stored on
// the model) and which jobs the checks open up.
public sealed class ModelTestRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");
    private static readonly CurrentUser Sam = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Sam");

    private readonly FixedClock clock = new(Now);
    private readonly FakeAiModelSettings settings = new();
    private readonly FakeRunStore runs = new();
    private readonly FakeScheduler scheduler = new();
    private readonly FakeSuite suite = new();
    private readonly StartModelTest start;
    private readonly RunModelTest run;

    public ModelTestRunTests()
    {
        settings.Stored = new AiModelSettings(
            [new AiModel("luna", "Luna", "gpt-6-luna", TemperatureMode.Usual, [], Gary.ObjectId, "Gary", Now)],
            new Dictionary<AiJob, ActiveSelection>());
        start = new StartModelTest(settings, runs, scheduler, new ModelTestLimits(), clock);
        var active = new ActiveAiModels(settings, clock, new ListLogger<ActiveAiModels>());
        run = new RunModelTest(
            runs, suite, new ManageAiModels(settings, active, clock, new ListLogger<ManageAiModels>()), clock, new ListLogger<RunModelTest>());
    }

    private static CheckResult Result(string id, string state, string? reason = null) => new(id, state, reason, 100);

    private static IReadOnlyList<CheckResult> All(string state, params (string Id, string State)[] changes) =>
        [.. CheckIds.All.Select(id => Result(id, changes.FirstOrDefault(c => c.Id == id).State ?? state))];

    [Fact]
    public async Task Starting_a_run_queues_it_and_returns_the_waiting_checks()
    {
        var result = Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken));

        Assert.Equal(RunStatuses.Queued, result.Run.Status);
        Assert.Equal(CheckIds.All, result.Run.Checks.Select(c => c.Id));
        Assert.All(result.Run.Checks, c => Assert.Equal(CheckStates.Waiting, c.State));
        Assert.Equal("gpt-6-luna", result.Run.Deployment);
        Assert.Equal(Gary.ObjectId, result.Run.StartedBy);
        Assert.Equal([result.Run.RunId], scheduler.Queued);
        Assert.Equal(result.Run.RunId, runs.Get(result.Run.RunId).RunId);
    }

    [Fact]
    public async Task A_second_start_while_one_is_going_returns_that_run_and_queues_nothing_new()
    {
        var first = Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken));

        var second = Assert.IsType<StartModelTestResult.AlreadyRunning>(await start.ExecuteAsync("luna", Sam, TestContext.Current.CancellationToken));

        Assert.Equal(first.Run.RunId, second.Run.RunId);
        Assert.Single(scheduler.Queued);
    }

    [Fact]
    public async Task A_run_that_finished_or_was_abandoned_doesnt_block_the_next()
    {
        var first = Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken));
        await runs.SaveAsync(first.Run with { Status = RunStatuses.Done }, TestContext.Current.CancellationToken);
        var second = Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken));

        clock.Now = Now.AddMinutes(30); // second never finished: abandoned
        var third = Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken));

        Assert.NotEqual(first.Run.RunId, second.Run.RunId);
        Assert.NotEqual(second.Run.RunId, third.Run.RunId);
    }

    [Fact]
    public async Task An_unknown_model_is_not_found()
    {
        Assert.IsType<StartModelTestResult.NotFound>(await start.ExecuteAsync("nope", Gary, TestContext.Current.CancellationToken));
        Assert.Empty(scheduler.Queued);
    }

    [Fact]
    public async Task The_sixth_run_in_an_hour_is_refused_and_the_limit_is_per_person()
    {
        for (var i = 0; i < 5; i++)
        {
            var started = Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken));
            await runs.SaveAsync(started.Run with { Status = RunStatuses.Done }, TestContext.Current.CancellationToken);
            clock.Now = clock.Now.AddMinutes(1);
        }

        var refused = Assert.IsType<StartModelTestResult.TooMany>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken));
        Assert.Equal(Now.AddHours(1), refused.RetryAt);
        Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Sam, TestContext.Current.CancellationToken));

        clock.Now = Now.AddHours(1).AddSeconds(1); // the first one is an hour old
        var started6 = Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken));
        await runs.SaveAsync(started6.Run with { Status = RunStatuses.Done }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Following_a_running_run_doesnt_use_up_the_limit()
    {
        Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken));
        for (var i = 0; i < 10; i++)
        {
            Assert.IsType<StartModelTestResult.AlreadyRunning>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_run_saves_each_check_as_it_finishes_then_stores_jobs_mode_and_the_run_on_the_model()
    {
        var queued = Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken)).Run;
        suite.Steps.Add([Result(CheckIds.Reachable, CheckStates.Pass), .. CheckIds.All.Skip(1).Select(CheckResult.Waiting)]);
        var final = All(CheckStates.Pass, (CheckIds.Temperature, CheckStates.Warn), (CheckIds.Photo, CheckStates.Fail));
        suite.Outcome = new SuiteOutcome(final, TemperatureMode.Default, "gpt-6-luna-2026-09-01", new RunCosts(0.01m, 0.002m, null, 0.05m, "meter"), 12_345);

        await run.ExecuteAsync(queued.RunId, TestContext.Current.CancellationToken);

        Assert.Equal(CheckStates.Pass, runs.Saves[0].Checks[0].State); // seen mid-run
        Assert.Equal(RunStatuses.Running, runs.Saves[0].Status);
        var done = runs.Get(queued.RunId);
        Assert.Equal(RunStatuses.Done, done.Status);
        Assert.Equal(Now, done.FinishedAt);
        Assert.Equal(["recipes", "genericNames"], done.Jobs);
        Assert.Equal("default", done.TemperatureMode);
        Assert.Equal("gpt-6-luna-2026-09-01", done.ModelVersion);
        Assert.Equal(12_345, done.TotalMs);
        var model = settings.Stored!.Find("luna")!;
        Assert.Equal([AiJob.Recipes, AiJob.GenericNames], model.Jobs);
        Assert.Equal(TemperatureMode.Default, model.TemperatureMode);
        Assert.Equal(queued.RunId.ToString(), (string?)model.Tests?["runId"]);
        Assert.Equal("Gary", (string?)model.Tests?["startedByName"]);
        Assert.Equal(0, runs.MarkCount);
    }

    [Fact]
    public async Task A_message_delivered_twice_runs_the_suite_once()
    {
        var queued = Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken)).Run;
        suite.Outcome = new SuiteOutcome(All(CheckStates.Pass), TemperatureMode.Usual, null, new RunCosts(null, null, null, null, null), 1);

        await run.ExecuteAsync(queued.RunId, TestContext.Current.CancellationToken);
        await run.ExecuteAsync(queued.RunId, TestContext.Current.CancellationToken);

        Assert.Equal(1, suite.Runs);
    }

    [Fact]
    public async Task An_unknown_run_is_ignored()
    {
        await run.ExecuteAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(0, suite.Runs);
    }

    [Fact]
    public async Task A_suite_that_crashes_leaves_a_failed_run_with_no_detail_and_clears_the_mark()
    {
        var queued = Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken)).Run;
        suite.Throw = new InvalidOperationException("secret prompt text");

        await run.ExecuteAsync(queued.RunId, TestContext.Current.CancellationToken);

        var failed = runs.Get(queued.RunId);
        Assert.Equal(RunStatuses.Failed, failed.Status);
        Assert.Equal("The run stopped unexpectedly.", failed.Error);
        Assert.DoesNotContain("secret", ModelTestJson.ToNode(failed).ToJsonString());
        Assert.Empty(settings.Stored!.Find("luna")!.Jobs);
        Assert.Equal(0, runs.MarkCount);
    }

    [Fact]
    public void Warnings_dont_block_a_job_but_failures_and_skips_do()
    {
        Assert.Equal(AiJobs.All, ModelTestJobs.Passed(All(CheckStates.Warn)));
        Assert.Equal([AiJob.Recipes, AiJob.GenericNames], ModelTestJobs.Passed(All(CheckStates.Pass, (CheckIds.Photo, CheckStates.Fail))));
        Assert.Equal([AiJob.Photos, AiJob.GenericNames], ModelTestJobs.Passed(All(CheckStates.Pass, (CheckIds.Adjust, CheckStates.Fail))));
        Assert.Empty(ModelTestJobs.Passed(All(CheckStates.Pass, (CheckIds.Reachable, CheckStates.Fail))));
        Assert.Empty(ModelTestJobs.Passed(All(CheckStates.Pass, (CheckIds.Speed, CheckStates.Fail))));
        Assert.Empty(ModelTestJobs.Passed(All(CheckStates.Waiting)));
        Assert.Equal(AiJobs.All, ModelTestJobs.Passed(All(CheckStates.Pass, (CheckIds.Cost, CheckStates.Fail)))); // cost gates nothing
        
    }

    [Fact]
    public async Task A_model_that_cant_read_images_cant_be_made_active_for_photos()
    {
        var queued = Assert.IsType<StartModelTestResult.Started>(await start.ExecuteAsync("luna", Gary, TestContext.Current.CancellationToken)).Run;
        suite.Outcome = new SuiteOutcome(
            All(CheckStates.Pass, (CheckIds.Photo, CheckStates.Fail)), TemperatureMode.Usual, null, new RunCosts(null, null, null, null, null), 1);
        await run.ExecuteAsync(queued.RunId, TestContext.Current.CancellationToken);
        var manage = new ManageAiModels(
            settings, new ActiveAiModels(settings, clock, new ListLogger<ActiveAiModels>()), clock, new ListLogger<ManageAiModels>());

        Assert.IsType<AiModelsResult.Conflict>(await manage.SetActiveAsync(AiJob.Photos, "luna", Gary, TestContext.Current.CancellationToken));
        Assert.IsType<AiModelsResult.Done>(await manage.SetActiveAsync(AiJob.Recipes, "luna", Gary, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Run_json_round_trips_with_utc_timestamps()
    {
        var run1 = ModelTestRun.Queued(Guid.NewGuid(), settings.Stored!.Models[0], Gary, Now);

        var json = ModelTestJson.ToNode(run1);

        Assert.Equal("2026-10-27T09:00:00.000Z", (string?)json["startedAt"]);
        Assert.Equal("queued", (string?)json["status"]);
        Assert.Equal(run1, ModelTestJson.FromNode(json)! with { Checks = run1.Checks, Jobs = run1.Jobs });
    }
}
