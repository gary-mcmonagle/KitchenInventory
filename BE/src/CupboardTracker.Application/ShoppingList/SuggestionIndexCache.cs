using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.ShoppingList;

/// <summary>One name the shopping list can suggest (#84): built from products (by generic name) and the list's memory.</summary>
public sealed record SuggestionCandidate(
    string Key, string Name, bool Generic, int InStock, int Uses, DateTimeOffset? LastUsed, Guid? ProductId);

/// <summary>From app settings (#109): SUGGESTION_CACHE_SECONDS (default 60), how long a built index is reused.</summary>
public sealed record SuggestionCacheSettings(TimeSpan Ttl)
{
    public static readonly SuggestionCacheSettings Default = new(TimeSpan.FromSeconds(60));
}

/// <summary>
/// The suggestion index, kept in memory on this instance of the API app (#109): building it reads every product, the
/// stock and the list memory (~265 RU), so it's built once and reused for <see cref="SuggestionCacheSettings.Ttl"/>
/// instead of on every keystroke. One build at a time: keystrokes arriving together wait for the same build. A write to
/// the list on this instance clears it (<see cref="Invalidate"/>); other instances catch up within the TTL, and stock
/// counts may lag by as much. Best-effort: a failed build isn't kept, so the next keystroke tries again. Singleton.
/// </summary>
public sealed partial class SuggestionIndexCache(SuggestionCacheSettings settings, TimeProvider clock, ILogger<SuggestionIndexCache> logger)
    : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile Entry? current; // a reference, so reads and swaps are atomic
    private long generation;

    public async Task<IReadOnlyList<SuggestionCandidate>> GetAsync(
        Func<CancellationToken, Task<IReadOnlyList<SuggestionCandidate>>> build, CancellationToken cancellationToken)
    {
        if (Fresh() is { } index)
        {
            return index;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh() is { } built)
            {
                return built; // another keystroke built it while this one waited
            }

            var started = Stopwatch.GetTimestamp();
            var seen = Interlocked.Read(ref generation);
            var result = await build(cancellationToken);
            if (Interlocked.Read(ref generation) == seen)
            {
                current = new Entry(result, clock.GetUtcNow(), seen); // not if the list changed while building: that copy may be stale
            }

            LogRebuilt(result.Count, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The list or its memory changed on this instance: the next keystroke builds afresh.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref generation);
        current = null;
    }

    public void Dispose() => gate.Dispose();

    private IReadOnlyList<SuggestionCandidate>? Fresh() =>
        current is { } c && c.Generation == Interlocked.Read(ref generation) && clock.GetUtcNow() - c.BuiltAt < settings.Ttl ? c.Index : null;

    private sealed record Entry(IReadOnlyList<SuggestionCandidate> Index, DateTimeOffset BuiltAt, long Generation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Suggestion index rebuilt: {Count} names in {Ms} ms.")]
    private partial void LogRebuilt(int count, int ms);
}
