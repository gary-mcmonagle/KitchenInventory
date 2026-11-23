namespace CupboardTracker.Application.Usuals;

/// <summary>How sure the "Add usuals" selection is that something is due (#134): <see cref="Due"/> is pre-ticked, <see cref="Maybe"/> is
/// listed but not ticked.</summary>
public enum UsualsBand
{
    Due,
    Maybe,
}

/// <summary>One unit checked in since the stock take, with what the usuals need to know about its product: a name (the generic name,
/// else the display name), its aisle and its generic name id. The same name across brands counts as one thing.</summary>
public sealed record UsualsCheckIn(string Key, string Name, string Aisle, Guid ProductId, string? GenericNameId, DateTimeOffset CheckedInAt);

/// <summary>A name bought in two or more of the weeks since the stock take. <see cref="Weeks"/> is one flag per complete Monday-to-Sunday
/// week, oldest first: was it checked in that week (spike #135).</summary>
public sealed record UsualsCandidate(
    string Key, string Name, string Aisle, Guid ProductId, string? GenericNameId, IReadOnlyList<bool> Weeks)
{
    public int WeeksBought => Weeks.Count(w => w);

    /// <summary>How many weekly shops ago it was last bought: 0 for the most recent week.</summary>
    public int WeeksSinceLast
    {
        get
        {
            for (var i = Weeks.Count - 1; i >= 0; i--)
            {
                if (Weeks[i])
                {
                    return Weeks.Count - 1 - i;
                }
            }

            return Weeks.Count;
        }
    }
}

/// <summary>One suggestion in the selection. <see cref="Why"/> is written by code from the facts, never by a model.</summary>
public sealed record UsualsItem(string Name, string? GenericNameId, string Aisle, UsualsBand Band, string Why, Guid ProductId);

public enum UsualsStatus
{
    Working,
    Ready,
    Failed,
}

/// <summary>The steps the sheet shows while a job runs (#134).</summary>
public static class UsualsSteps
{
    public const string History = "history";
    public const string Deciding = "deciding";
    public const string List = "list";
}

/// <summary><see cref="Method"/> is <c>jev</c> or <c>pattern</c> (the plain rule, when Jev is off or fails); null until ready.</summary>
public sealed record UsualsJob(
    Guid JobId, Guid UserId, UsualsStatus Status, string Step, string? Method, IReadOnlyList<UsualsItem> Items, DateTimeOffset CreatedAt);

public static class UsualsMethods
{
    public const string Jev = "jev";
    public const string Pattern = "pattern";
}

/// <summary>From app settings; defaults from spike #135. Due at 0.8 and Maybe at 0.6 are Jev's probabilities; the pattern fallback uses the
/// share of weeks (0.75 and 0.5).</summary>
public sealed record UsualsSettings(
    double DueConfidence = 0.8,
    double MaybeConfidence = 0.6,
    double PatternDue = 0.75,
    double PatternMaybe = 0.5,
    int RequestsPerHour = 10,
    TimeSpan JobBudget = default,
    int JevParallelism = 4,
    int MinWeeks = 2)
{
    public TimeSpan Budget => JobBudget == default ? TimeSpan.FromSeconds(60) : JobBudget;
}

public interface IUsualsHistory
{
    /// <summary>Check-ins since <paramref name="since"/>, whatever their source (scanner, app Add, camera), leaving out check-ins that were
    /// undone (#133). Stock is never read.</summary>
    Task<IReadOnlyList<UsualsCheckIn>> GetCheckInsAsync(DateTimeOffset since, CancellationToken cancellationToken);
}

public abstract record UsualsJudgement
{
    /// <summary>Jev's probability that each candidate is due, in the order given.</summary>
    public sealed record Judged(IReadOnlyList<double> Probabilities, int InputTokens = 0) : UsualsJudgement;

    /// <summary>Not configured, a timeout, or a failed request: the plain rule is used instead.</summary>
    public sealed record Unavailable(string Reason) : UsualsJudgement;
}

public interface IUsualsJudge
{
    Task<UsualsJudgement> JudgeAsync(IReadOnlyList<UsualsCandidate> candidates, CancellationToken cancellationToken);
}

public interface IUsualsJobRepository
{
    Task CreateAsync(UsualsJob job, CancellationToken cancellationToken);

    Task<Versioned<UsualsJob>?> GetAsync(Guid jobId, CancellationToken cancellationToken);

    Task<bool> TryReplaceAsync(Versioned<UsualsJob> current, UsualsJob updated, CancellationToken cancellationToken);
}

/// <summary>When to work out a new job: a <c>{ jobId }</c> message on the <c>usuals-jobs</c> queue (Infrastructure).</summary>
public interface IUsualsJobScheduler
{
    Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken);
}

/// <summary>At most <see cref="UsualsSettings.RequestsPerHour"/> runs per person an hour, in memory like the plan limit.</summary>
public sealed class UsualsRateLimit(UsualsSettings settings, TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> calls = [];
    private readonly Lock gate = new();

    public bool TryTake(Guid userId)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (!calls.TryGetValue(userId, out var times))
            {
                calls[userId] = times = new Queue<DateTimeOffset>();
            }

            while (times.Count > 0 && now - times.Peek() >= Window)
            {
                times.Dequeue();
            }

            if (times.Count >= settings.RequestsPerHour)
            {
                return false;
            }

            times.Enqueue(now);
            return true;
        }
    }
}
