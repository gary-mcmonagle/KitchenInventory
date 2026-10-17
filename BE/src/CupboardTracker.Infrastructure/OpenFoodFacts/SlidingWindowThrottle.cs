namespace CupboardTracker.Infrastructure.OpenFoodFacts;

/// <summary>At most <c>limit</c> starts per rolling minute. Callers over the limit wait for a slot (queue, never fail).</summary>
public sealed class SlidingWindowThrottle(int limit, TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Queue<DateTimeOffset> starts = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    public int Limit { get; } = limit;

    public void Dispose() => gate.Dispose();

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                var now = clock.GetUtcNow();
                while (starts.Count > 0 && now - starts.Peek() >= Window)
                {
                    starts.Dequeue();
                }

                if (starts.Count < Limit)
                {
                    starts.Enqueue(now);
                    return;
                }

                await Task.Delay(starts.Peek() + Window - now, clock, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
