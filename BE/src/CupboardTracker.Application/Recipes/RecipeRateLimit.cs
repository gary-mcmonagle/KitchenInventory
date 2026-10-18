namespace CupboardTracker.Application.Recipes;

/// <summary>From app settings: RECIPE_REQUESTS_PER_HOUR (default 30), the cost guard on recipe suggestions (#73).</summary>
public sealed record RecipeSettings(int RequestsPerHour = RecipeSettings.DefaultRequestsPerHour)
{
    public const int DefaultRequestsPerHour = 30;
}

/// <summary>
/// At most <see cref="RecipeSettings.RequestsPerHour"/> recipe requests per user in any hour (#73). Kept in memory, so
/// it's per instance of the API app: at household volume there's one, and a restart forgets the counts. That's fine
/// for a cost guard.
/// </summary>
public sealed class RecipeRateLimit(RecipeSettings settings, TimeProvider clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> requests = [];
    private readonly Lock gate = new();

    /// <summary>Counts the request and says whether it's allowed. A refused request doesn't count.</summary>
    public bool TryTake(Guid userId)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (!requests.TryGetValue(userId, out var times))
            {
                requests[userId] = times = new Queue<DateTimeOffset>();
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
