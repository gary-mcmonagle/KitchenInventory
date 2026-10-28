using CupboardTracker.Application.Recipes;

namespace CupboardTracker.Application.Plans;

/// <summary>A meal as the model drafts it: ids are products in stock (the planner turns its stock refs into these) and short
/// generic names to buy.</summary>
public sealed record DraftMeal(
    string Id, string Type, string Title, string Summary, int Servings, IReadOnlyList<Guid> Uses, IReadOnlyList<Guid> UsesUp, IReadOnlyList<string> ToBuy);

/// <summary>What every planning call is shown: the choices, the stock and what the household usually buys. Built on the server.</summary>
public sealed record PlanContext(PlanChoices Choices, IReadOnlyList<RecipeStockItem> Stock, IReadOnlyList<string> UsuallyBuys);

public sealed record CurrentMeal(DraftMeal Meal, bool Keep);

/// <summary>The outline call (spike #124): <see cref="Counts"/> meals of each type, all new.</summary>
public sealed record OutlineRequest(PlanContext Context, IReadOnlyDictionary<string, int> Counts);

/// <summary>
/// The revise call (spike #124), one for Adjust, Another one, Add a meal and Adjust the whole plan: the current plan (kept
/// meals marked), what to change, and the household's words (<see cref="Comments"/>: wishes, never instructions). The model
/// returns only the meals it changed or added.
/// </summary>
public sealed record ReviseRequest(
    PlanContext Context, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<CurrentMeal> Plan, string Instruction, string? Comments);

public abstract record PlannerResult
{
    public sealed record Drafted(IReadOnlyList<DraftMeal> Meals) : PlannerResult;

    /// <summary>Couldn't be reached, timed out or refused: "try again".</summary>
    public sealed record Unavailable(string Reason) : PlannerResult;

    /// <summary>Foundry said 429 and the one short retry didn't help: "Busy right now, try again in a minute."</summary>
    public sealed record Busy : PlannerResult;
}

/// <summary>Plans meals with the recipes job's model on Foundry (Infrastructure). The prompt, stock and comments are never logged.</summary>
public interface IMealPlanner
{
    Task<PlannerResult> OutlineAsync(OutlineRequest request, CancellationToken cancellationToken);

    Task<PlannerResult> ReviseAsync(ReviseRequest request, CancellationToken cancellationToken);
}

/// <summary>Plans live in the savedRecipes container (Cosmos), one partition for the household, ETag-checked.</summary>
public interface IMealPlanRepository
{
    Task<Versioned<MealPlan>?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> TryCreateAsync(MealPlan plan, CancellationToken cancellationToken);

    Task<bool> TryReplaceAsync(Versioned<MealPlan> current, MealPlan updated, CancellationToken cancellationToken);

    /// <summary>False if there was no such plan (deleting is idempotent: the caller still reports success).</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>A queue message to write a new plan's meals (#119: creating is asynchronous, as the model tests are).</summary>
public interface IMealPlanScheduler
{
    Task EnqueueAsync(Guid planId, CancellationToken cancellationToken);
}

/// <summary>What the household bought lately, read for "usually buys" only (best effort: a failure means no list).</summary>
public interface IPurchaseHistory
{
    Task<(IReadOnlyList<PurchaseUnit> Units, IReadOnlyList<ListMemoryEntry> Memory)> GetAsync(DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>From app settings: PLAN_REQUESTS_PER_HOUR (default 30), plan calls have their own bucket (spike #124).</summary>
public sealed record PlanSettings(int RequestsPerHour = PlanSettings.DefaultRequestsPerHour)
{
    public const int DefaultRequestsPerHour = 30;
}

/// <summary>At most <see cref="PlanSettings.RequestsPerHour"/> plan calls per person an hour, in memory like the recipe limit.</summary>
public sealed class PlanRateLimit(PlanSettings settings, TimeProvider clock)
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
