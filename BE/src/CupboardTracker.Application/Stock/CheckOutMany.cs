using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

/// <summary>What happened to one unit of a clear out: <c>checkedOut</c> (this request took it), <c>unchanged</c> (it was
/// already checked out, or there is no such unit: nothing changed, not an error) or <c>failed</c> (it could not be saved;
/// try again).</summary>
public enum ClearOutOutcome
{
    CheckedOut,
    Unchanged,
    Failed,
}

public sealed record ClearOutUnitResult(Guid UnitId, ClearOutOutcome Outcome, Guid? ProductId = null);

public abstract record CheckOutManyResult
{
    public sealed record Done(IReadOnlyList<ClearOutUnitResult> Units) : CheckOutManyResult
    {
        public int CheckedOut => Units.Count(u => u.Outcome == ClearOutOutcome.CheckedOut);

        public int Unchanged => Units.Count(u => u.Outcome == ClearOutOutcome.Unchanged);

        public int Failed => Units.Count(u => u.Outcome == ClearOutOutcome.Failed);
    }

    /// <summary>400: no units, too many, or the same unit twice.</summary>
    public sealed record Invalid(string Error) : CheckOutManyResult;
}

/// <summary>
/// Clear out (#132): checks out a list of units in one request. Every unit goes through the existing
/// <see cref="CheckOutUnit"/>, so the scan feed, run-out, pushes and logging behave as for a single check-out; there is no
/// second way to check out. A unit already checked out, or one that doesn't exist, changes nothing and is not an error
/// (so a replayed request is harmless); a unit that fails to save doesn't stop the others. Undo is the existing
/// <see cref="UndoCheckOut"/>, called by the app for the units this request checked out.
/// </summary>
public sealed partial class CheckOutMany(CheckOutUnit checkOut, ILogger<CheckOutMany> logger)
{
    public const int MaxUnits = 300;

    public async Task<CheckOutManyResult> ExecuteAsync(IReadOnlyList<Guid>? unitIds, CurrentUser user, CancellationToken cancellationToken)
    {
        if (unitIds is not { Count: > 0 })
        {
            return new CheckOutManyResult.Invalid("unitIds must list at least one unit.");
        }

        if (unitIds.Count > MaxUnits)
        {
            return new CheckOutManyResult.Invalid($"unitIds can list at most {MaxUnits} units.");
        }

        if (unitIds.Distinct().Count() != unitIds.Count)
        {
            return new CheckOutManyResult.Invalid("unitIds must not repeat a unit.");
        }

        var results = new List<ClearOutUnitResult>(unitIds.Count);
        foreach (var unitId in unitIds)
        {
            results.Add(await CheckOneAsync(unitId, user, cancellationToken));
        }

        var done = new CheckOutManyResult.Done(results);
        LogDone(unitIds.Count, done.CheckedOut, done.Unchanged, done.Failed);
        return done;
    }

    private async Task<ClearOutUnitResult> CheckOneAsync(Guid unitId, CurrentUser user, CancellationToken cancellationToken)
    {
        try
        {
            return await checkOut.ExecuteAsync(unitId, user, cancellationToken) switch
            {
                CheckOutUnitResult.CheckedOut c => new ClearOutUnitResult(unitId, ClearOutOutcome.CheckedOut, c.ProductId),
                _ => new ClearOutUnitResult(unitId, ClearOutOutcome.Unchanged),
            };
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Counts only in the log (never names); the reason is on the exception telemetry of the dependency call.
            return new ClearOutUnitResult(unitId, ClearOutOutcome.Failed);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Clear out: {Requested} units requested, {CheckedOut} checked out, {Unchanged} unchanged, {Failed} failed.")]
    private partial void LogDone(int requested, int checkedOut, int unchanged, int failed);
}
