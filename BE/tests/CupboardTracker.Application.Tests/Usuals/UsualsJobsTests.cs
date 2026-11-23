using CupboardTracker.Application.Plans;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Application.Tests.ShoppingList;
using CupboardTracker.Application.Users;
using CupboardTracker.Application.Usuals;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Usuals;

internal sealed class FakeUsualsHistory : IUsualsHistory
{
    public List<UsualsCheckIn> CheckIns { get; } = [];

    public bool Fails { get; set; }

    public Task<IReadOnlyList<UsualsCheckIn>> GetCheckInsAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        Fails ? throw new InvalidOperationException("storage down") : Task.FromResult<IReadOnlyList<UsualsCheckIn>>([.. CheckIns]);
}

internal sealed class FakeUsualsJudge : IUsualsJudge
{
    public Func<IReadOnlyList<UsualsCandidate>, CancellationToken, Task<UsualsJudgement>>? Answer { get; set; }

    public List<IReadOnlyList<string>> Asked { get; } = [];

    public Task<UsualsJudgement> JudgeAsync(IReadOnlyList<UsualsCandidate> candidates, CancellationToken cancellationToken)
    {
        Asked.Add([.. candidates.Select(c => c.Key)]);
        return Answer is null
            ? Task.FromResult<UsualsJudgement>(new UsualsJudgement.Judged([.. candidates.Select(_ => 0.9)], 100))
            : Answer(candidates, cancellationToken);
    }
}

internal sealed class FakeUsualsJobs : IUsualsJobRepository
{
    private int etag;

    public Dictionary<Guid, Versioned<UsualsJob>> Jobs { get; } = [];

    public List<string> Steps { get; } = [];

    public Task CreateAsync(UsualsJob job, CancellationToken cancellationToken)
    {
        Jobs[job.JobId] = new(job, $"e{++etag}");
        return Task.CompletedTask;
    }

    public Task<Versioned<UsualsJob>?> GetAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(Jobs.GetValueOrDefault(jobId));

    public Task<bool> TryReplaceAsync(Versioned<UsualsJob> current, UsualsJob updated, CancellationToken cancellationToken)
    {
        if (Jobs.GetValueOrDefault(updated.JobId)?.ETag != current.ETag)
        {
            return Task.FromResult(false);
        }

        Jobs[updated.JobId] = new(updated, $"e{++etag}");
        Steps.Add($"{updated.Status}:{updated.Step}");
        return Task.FromResult(true);
    }
}

internal sealed class FakeUsualsScheduler : IUsualsJobScheduler
{
    public List<Guid> Queued { get; } = [];

    public Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken)
    {
        Queued.Add(jobId);
        return Task.CompletedTask;
    }
}

public sealed class UsualsJobsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");

    private readonly FakeUsualsHistory history = new();
    private readonly FakeShoppingList list = new();
    private readonly FakeUsualsJudge judge = new();
    private readonly FakeUsualsJobs jobs = new();
    private readonly FakeUsualsScheduler scheduler = new();
    private readonly ListLogger<UsualsJobs> log = new();
    private UsualsSettings settings = new();

    private UsualsJobs Usuals() => new(
        history, list, judge, jobs, scheduler, new UsualsRateLimit(settings, new FixedClock(Now)), settings, new UsuallyBuysSettings(Start),
        TestSettings.Stock(), new FixedClock(Now), log);

    private static UsualsCheckIn In(string name, int month, int day) =>
        new(name.ToLowerInvariant(), name, "dairyEggs", Guid.NewGuid(), null, new DateTimeOffset(2026, month, day, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Bought in two different weeks, so it's a candidate.</summary>
    private void Bought(params string[] names)
    {
        foreach (var name in names)
        {
            history.CheckIns.Add(In(name, 10, 20));
            history.CheckIns.Add(In(name, 11, 10));
        }
    }

    private void OnList(string name, ListItemStatus status = ListItemStatus.Open, bool removed = false)
    {
        var item = new ShoppingListItem(Guid.NewGuid(), name, ShoppingListNames.Normalize(name), null, null, "other", "jev", "typed", status,
            Gary.ObjectId, "Gary", Now, null, null, null, Now, 1, "c", removed);
        list.Items[item.Id] = new(item, "e1");
    }

    private async Task<UsualsJob> Run()
    {
        var started = Assert.IsType<UsualsStartResult.Started>(await Usuals().StartAsync(Gary, CancellationToken.None));
        await Usuals().RunAsync(started.Job.JobId, CancellationToken.None);
        return (await Usuals().GetAsync(started.Job.JobId, CancellationToken.None))!;
    }

    [Fact]
    public async Task Starting_writes_a_working_job_and_queues_it()
    {
        var started = Assert.IsType<UsualsStartResult.Started>(await Usuals().StartAsync(Gary, CancellationToken.None));

        Assert.Equal((UsualsStatus.Working, UsualsSteps.History, Gary.ObjectId), (started.Job.Status, started.Job.Step, started.Job.UserId));
        Assert.Equal([started.Job.JobId], scheduler.Queued);
        Assert.Equal(UsualsStatus.Working, (await Usuals().GetAsync(started.Job.JobId, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_person_can_start_only_so_many_an_hour()
    {
        settings = new UsualsSettings(RequestsPerHour: 2);
        var usuals = Usuals();

        Assert.IsType<UsualsStartResult.Started>(await usuals.StartAsync(Gary, CancellationToken.None));
        Assert.IsType<UsualsStartResult.Started>(await usuals.StartAsync(Gary, CancellationToken.None));
        Assert.IsType<UsualsStartResult.RateLimited>(await usuals.StartAsync(Gary, CancellationToken.None));
        Assert.Equal(2, scheduler.Queued.Count);
        Assert.IsType<UsualsStartResult.Started>(await usuals.StartAsync(new CurrentUser(Guid.NewGuid(), "Sam"), CancellationToken.None)); // its own limit
    }

    [Fact]
    public async Task Asks_Jev_only_about_what_isnt_on_the_list_and_offers_the_rest_as_due()
    {
        Bought("Milk", "Eggs", "Spinach", "Butter", "Rice");
        OnList("Milk");
        OnList("EGGS", ListItemStatus.Done); // ticked: still on the list
        OnList("Rice", removed: true); // removed: gone, so it can come back

        var job = await Run();

        Assert.Equal([["butter", "rice", "spinach"]], judge.Asked);
        Assert.Equal((UsualsStatus.Ready, UsualsMethods.Jev), (job.Status, job.Method));
        Assert.Equal(["Butter", "Rice", "Spinach"], job.Items.Select(i => i.Name).Order());
        Assert.All(job.Items, i => Assert.Equal(UsualsBand.Due, i.Band));
        Assert.Equal("Bought in 2 of the last 5 weeks", job.Items[0].Why);
    }

    [Fact]
    public async Task Leaves_out_something_added_to_the_list_while_Jev_was_thinking()
    {
        Bought("Milk", "Eggs");
        judge.Answer = (candidates, _) =>
        {
            OnList("Eggs");
            return Task.FromResult<UsualsJudgement>(new UsualsJudgement.Judged([.. candidates.Select(_ => 0.9)]));
        };

        var job = await Run();

        Assert.Equal(["Milk"], job.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Uses_the_plain_rule_when_Jev_is_unavailable_and_says_so()
    {
        Bought("Milk", "Eggs");
        history.CheckIns.Add(In("Milk", 11, 17)); // three of five weeks, so Maybe or better
        judge.Answer = (_, _) => Task.FromResult<UsualsJudgement>(new UsualsJudgement.Unavailable("not configured"));

        var job = await Run();

        Assert.Equal((UsualsStatus.Ready, UsualsMethods.Pattern), (job.Status, job.Method));
        Assert.Equal(["Milk"], job.Items.Select(i => i.Name)); // 3 of 5 = 0.6: Maybe; eggs 2 of 5 = 0.4: left out
        Assert.Equal(UsualsBand.Maybe, job.Items[0].Band);
        Assert.Contains(log.At(LogLevel.Information), m => m.Contains("plain rule"));
    }

    [Fact]
    public async Task Uses_the_plain_rule_when_Jev_runs_over_the_time_budget()
    {
        Bought("Milk");
        settings = new UsualsSettings(JobBudget: TimeSpan.FromMilliseconds(50));
        judge.Answer = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new UsualsJudgement.Unavailable("never");
        };

        var job = await Run();

        Assert.Equal((UsualsStatus.Ready, UsualsMethods.Pattern), (job.Status, job.Method));
    }

    [Fact]
    public async Task A_job_that_cannot_read_the_history_fails_and_logs_an_error_without_names()
    {
        Bought("Milk");
        history.Fails = true;

        var job = await Run();

        Assert.Equal((UsualsStatus.Failed, Array.Empty<UsualsItem>().Length), (job.Status, job.Items.Count));
        Assert.Single(log.At(LogLevel.Error));
        Assert.Empty(judge.Asked);
    }

    [Fact]
    public async Task With_nothing_to_judge_it_is_ready_with_no_items_and_asks_nobody()
    {
        history.CheckIns.Add(In("Milk", 10, 20)); // one week only

        var job = await Run();

        Assert.Equal((UsualsStatus.Ready, 0), (job.Status, job.Items.Count));
        Assert.Empty(judge.Asked);
    }

    [Fact]
    public async Task The_selection_is_not_capped()
    {
        Bought([.. Enumerable.Range(0, 130).Select(i => $"Thing {i:000}")]);

        var job = await Run();

        Assert.Equal(130, job.Items.Count);
    }

    [Fact]
    public async Task Goes_through_the_steps_and_a_second_delivery_of_the_message_changes_nothing()
    {
        Bought("Milk");
        var started = Assert.IsType<UsualsStartResult.Started>(await Usuals().StartAsync(Gary, CancellationToken.None));

        await Usuals().RunAsync(started.Job.JobId, CancellationToken.None);
        var steps = jobs.Steps.ToList();
        await Usuals().RunAsync(started.Job.JobId, CancellationToken.None);

        Assert.Equal(["Working:deciding", "Working:list", "Ready:list"], steps);
        Assert.Equal(steps, jobs.Steps);
        Assert.Single(judge.Asked);
    }

    [Fact]
    public async Task The_log_holds_counts_and_timings_only()
    {
        Bought("Milk", "Eggs");

        await Run();

        var line = Assert.Single(log.At(LogLevel.Information));
        Assert.StartsWith("Usuals: 2 candidates, 2 not already on the list, 2 offered (2 due, 0 maybe), method jev, 100 Jev input tokens", line);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("Milk", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("Eggs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_unknown_job_is_not_found()
    {
        Assert.Null(await Usuals().GetAsync(Guid.NewGuid(), CancellationToken.None));
        await Usuals().RunAsync(Guid.NewGuid(), CancellationToken.None); // nothing to do, no error
        Assert.Empty(judge.Asked);
    }
}
