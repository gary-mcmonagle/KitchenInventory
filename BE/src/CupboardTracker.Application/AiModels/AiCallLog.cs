namespace CupboardTracker.Application.AiModels;

/// <summary>
/// One call to a Foundry model, as the callers saw it (#121): numbers and Foundry's own error code and message, never the
/// prompt, the image or the reply. <see cref="Outcome"/> is <c>ok</c>, <c>refused</c> (a 4xx other than 429),
/// <c>filtered</c> (content filter), <c>timeout</c>, <c>network</c>, <c>unreadable</c> or <c>failed</c> (429 or 5xx).
/// </summary>
public sealed record AiCall(
    string Deployment, int Ms, int PromptTokens, int CompletionTokens, int? Status, string? Code, string? Message,
    string? ModelVersion, string Outcome)
{
    public const string Ok = "ok";
    public const string Refused = "refused";
    public const string Filtered = "filtered";
    public const string Timeout = "timeout";
    public const string Network = "network";
    public const string Unreadable = "unreadable";
    public const string Failed = "failed";
}

/// <summary>
/// Collects the Foundry calls made in one async flow (like <c>CosmosUsage</c>), so the model test suite (#121) can read
/// each real caller's tokens, time and refusal reason without the callers' results carrying them. Outside a
/// <see cref="Begin"/> scope, <see cref="Record"/> does nothing.
/// </summary>
public sealed class AiCallLog
{
    private static readonly AsyncLocal<AiCallLog?> CurrentLog = new();

    private readonly List<AiCall> calls = [];

    public static AiCallLog? Current => CurrentLog.Value;

    public IReadOnlyList<AiCall> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public static Scope Begin()
    {
        var log = new AiCallLog();
        var previous = CurrentLog.Value;
        CurrentLog.Value = log;
        return new Scope(log, previous);
    }

    public static void Record(AiCall call)
    {
        if (CurrentLog.Value is { } log)
        {
            lock (log.calls)
            {
                log.calls.Add(call);
            }
        }
    }

    public sealed class Scope(AiCallLog log, AiCallLog? previous) : IDisposable
    {
        public AiCallLog Log => log;

        public void Dispose() => CurrentLog.Value = previous;
    }
}
