using System.Diagnostics;
using CupboardTracker.Application.Plans;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Usuals;

public abstract record UsualsStartResult
{
    public sealed record Started(UsualsJob Job) : UsualsStartResult;

    public sealed record RateLimited : UsualsStartResult;
}

/// <summary>
/// "Add usuals" (#134): starting a job, reading it, and working it out. Like Plan meals the work is asynchronous: starting writes the job
/// and queues a message, the queue trigger runs <see cref="RunAsync"/>, and the sheet polls the job for its step and result. It reads the
/// buying history (never stock), leaves out names already on the list, asks Jev (one yes/no question per candidate) and falls back to the
/// share of weeks when Jev isn't available. The log holds counts and timings only.
/// </summary>
public sealed partial class UsualsJobs(
    IUsualsHistory history, IShoppingListRepository list, IUsualsJudge judge, IUsualsJobRepository jobs, IUsualsJobScheduler queue,
    UsualsRateLimit limit, UsualsSettings settings, UsuallyBuysSettings historySettings, StockSettings stock, TimeProvider clock,
    ILogger<UsualsJobs> logger)
{
    private const int MaxAttempts = 5;

    public async Task<UsualsStartResult> StartAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        if (!limit.TryTake(user.ObjectId))
        {
            return new UsualsStartResult.RateLimited();
        }

        var job = new UsualsJob(Guid.NewGuid(), user.ObjectId, UsualsStatus.Working, UsualsSteps.History, null, [], clock.GetUtcNow());
        await jobs.CreateAsync(job, cancellationToken);
        await queue.EnqueueAsync(job.JobId, cancellationToken);
        return new UsualsStartResult.Started(job);
    }

    public async Task<UsualsJob?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        (await jobs.GetAsync(jobId, cancellationToken))?.Value;

    /// <summary>Works a job out. A job that isn't working any more (a message delivered twice) is left alone.</summary>
    public async Task RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if ((await jobs.GetAsync(jobId, cancellationToken))?.Value is not { Status: UsualsStatus.Working })
        {
            return;
        }

        var started = Stopwatch.StartNew();
        try
        {
            var now = clock.GetUtcNow();
            var checkIns = await history.GetCheckInsAsync(historySettings.Start, cancellationToken);
            var candidates = UsualsFacts.Candidates(checkIns, historySettings.Start, now, stock.HomeTimeZone, settings.MinWeeks);
            var onList = await OnListAsync(cancellationToken);
            var open = candidates.Where(c => !onList.Contains(c.Key)).ToList();

            await SetStepAsync(jobId, UsualsSteps.Deciding, cancellationToken);
            var (method, items, tokens) = await DecideAsync(open, cancellationToken);

            await SetStepAsync(jobId, UsualsSteps.List, cancellationToken);
            onList = await OnListAsync(cancellationToken); // checked again: someone may have added one meanwhile
            items = [.. items.Where(i => !onList.Contains(ShoppingListNames.Normalize(i.Name)))];

            await FinishAsync(jobId, UsualsStatus.Ready, method, items, cancellationToken);
            LogReady(candidates.Count, open.Count, items.Count, items.Count(i => i.Band == UsualsBand.Due),
                items.Count(i => i.Band == UsualsBand.Maybe), method, tokens, started.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LogFailed(e, jobId, started.ElapsedMilliseconds);
            await FinishAsync(jobId, UsualsStatus.Failed, null, [], CancellationToken.None);
        }
    }

    private async Task<(string Method, IReadOnlyList<UsualsItem> Items, int Tokens)> DecideAsync(
        List<UsualsCandidate> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return (UsualsMethods.Jev, [], 0);
        }

        UsualsJudgement judgement;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(settings.Budget);
        try
        {
            judgement = await judge.JudgeAsync(candidates, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            judgement = new UsualsJudgement.Unavailable("over the time budget");
        }

        if (judgement is UsualsJudgement.Judged { Probabilities: var probabilities, InputTokens: var tokens } && probabilities.Count == candidates.Count)
        {
            return (UsualsMethods.Jev, UsualsFacts.ByJev(candidates, probabilities, settings), tokens);
        }

        LogFallback(judgement is UsualsJudgement.Unavailable u ? u.Reason : "unexpected answer");
        return (UsualsMethods.Pattern, UsualsFacts.ByPattern(candidates, settings), 0);
    }

    /// <summary>The normalized names of everything on the list now, open or ticked (a removed item is gone).</summary>
    private async Task<HashSet<string>> OnListAsync(CancellationToken cancellationToken) =>
        [.. (await list.ListAsync(cancellationToken)).Where(i => !i.Value.Removed).Select(i => i.Value.NormalizedName)];

    private Task SetStepAsync(Guid jobId, string step, CancellationToken cancellationToken) =>
        UpdateAsync(jobId, j => j with { Step = step }, cancellationToken);

    private Task FinishAsync(Guid jobId, UsualsStatus status, string? method, IReadOnlyList<UsualsItem> items, CancellationToken cancellationToken) =>
        UpdateAsync(jobId, j => j with { Status = status, Method = method, Items = items, Step = UsualsSteps.List }, cancellationToken);

    private async Task UpdateAsync(Guid jobId, Func<UsualsJob, UsualsJob> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (await jobs.GetAsync(jobId, cancellationToken) is not { } current)
            {
                return;
            }

            if (await jobs.TryReplaceAsync(current, change(current.Value), cancellationToken))
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Usuals: {Candidates} candidates, {Asked} not already on the list, {Offered} offered ({Due} due, {Maybe} maybe), method {Method}, {Tokens} Jev input tokens, {Ms} ms.")]
    private partial void LogReady(int candidates, int asked, int offered, int due, int maybe, string method, int tokens, long ms);

    [LoggerMessage(Level = LogLevel.Information, Message = "Usuals: using the plain rule instead of Jev ({Reason}).")]
    private partial void LogFallback(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Usuals job {JobId} failed after {Ms} ms.")]
    private partial void LogFailed(Exception exception, Guid jobId, long ms);
}
