using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Plans;

public abstract record PlanOutcome
{
    /// <summary>200 (or 202 for a plan just created, still being made): the plan as the app shows it.</summary>
    public sealed record Done(MealPlanView View) : PlanOutcome;

    /// <summary>A meal deleted: the plan, and the meal as it was so Undo can put it back.</summary>
    public sealed record MealDeleted(MealPlanView View, PlannedMeal Meal) : PlanOutcome;

    public sealed record Invalid(string Error) : PlanOutcome;

    public sealed record NotFound(string Error) : PlanOutcome;

    /// <summary>409: the plan is still being made, or its making failed.</summary>
    public sealed record NotReady(string Error) : PlanOutcome;

    /// <summary>409: someone else changed the plan while this was running.</summary>
    public sealed record Conflict : PlanOutcome;

    public sealed record NothingInStock : PlanOutcome;

    public sealed record TooManyRequests : PlanOutcome;

    /// <summary>"Busy right now, try again in a minute."</summary>
    public sealed record Busy : PlanOutcome;

    public sealed record Unavailable : PlanOutcome;
}

/// <summary>
/// Plan meals (#119, built on spike #124): a pool of meals made from the stock with one outline call, changed with one revise
/// call, and bookkeeping in code (the overlap score with one retry, what's shared, kept meals, warnings). Stock and history
/// are read on the server, never sent by the app. A failed call leaves the plan as it was. Only metadata is logged: never the
/// prompt, stock, comments, titles or items.
/// </summary>
public sealed partial class MealPlans(
    IMealPlanRepository repository, IMealPlanScheduler scheduler, IMealPlanner planner, GetStock stock, IPurchaseHistory history,
    UsuallyBuysSettings usuallyBuys, PlanRateLimit limit, TimeProvider clock, ILogger<MealPlans> logger)
{
    public const int MaxCommentsLength = 300;
    public const int MaxDays = 7;
    public const int MaxToBuyPerMeal = 4;
    public const int MaxMeals = 40;

    // ---- Create and generate

    /// <summary>Validates the choices and queues the plan (202): the meals are written by <see cref="GenerateAsync"/>.</summary>
    public async Task<PlanOutcome> CreateAsync(PlanChoices choices, CurrentUser user, CancellationToken cancellationToken)
    {
        if (Validate(choices) is { } problem)
        {
            return new PlanOutcome.Invalid(problem);
        }

        choices = choices with { Comments = string.IsNullOrWhiteSpace(choices.Comments) ? null : choices.Comments.Trim() };
        if (!limit.TryTake(user.ObjectId))
        {
            LogRateLimited();
            return new PlanOutcome.TooManyRequests();
        }

        var now = clock.GetUtcNow();
        var plan = new MealPlan(Guid.NewGuid(), user.ObjectId, user.DisplayName, now, now, PlanStatuses.Generating, null, choices, []);
        if (!await repository.TryCreateAsync(plan, cancellationToken))
        {
            return new PlanOutcome.Unavailable();
        }

        await scheduler.EnqueueAsync(plan.Id, cancellationToken);
        LogCreated(choices.Days, choices.TotalMeals, choices.Diet, choices.PreferUseFirst, choices.Comments is not null);
        return new PlanOutcome.Done(ToView(plan));
    }

    /// <summary>The queue-triggered job: writes the meals of a plan that's still being made. A message delivered twice is
    /// harmless (the second finds the plan done).</summary>
    public async Task GenerateAsync(Guid planId, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(planId, cancellationToken) is not { } current || current.Value.Status != PlanStatuses.Generating)
        {
            return;
        }

        var plan = current.Value;
        var started = clock.GetTimestamp();
        string? failure = null;
        IReadOnlyList<PlannedMeal> meals = [];
        var orphansBefore = 0;
        var retried = false;
        try
        {
            if (await BuildWorldAsync(plan.Choices, cancellationToken) is not { } world)
            {
                failure = "There's nothing in stock to plan from.";
            }
            else
            {
                var counts = Counts(plan.Choices);
                var outlined = await planner.OutlineAsync(new OutlineRequest(world.Context, counts), cancellationToken);
                if (outlined is not PlannerResult.Drafted drafted)
                {
                    failure = outlined is PlannerResult.Busy ? BusyText : UnavailableText;
                }
                else
                {
                    meals = Assemble(drafted.Meals, counts, world, plan.Choices);
                    orphansBefore = PlanOverlap.Orphans(meals).Count;
                    if (orphansBefore > 0 && meals.Count > 1)
                    {
                        retried = true;
                        meals = await ShareRetryAsync(plan.Choices, world, meals, cancellationToken);
                    }
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogGenerateFailed(e);
            failure = UnavailableText;
        }

        var finished = failure is null && meals.Count > 0
            ? plan with { Status = PlanStatuses.Ready, Meals = meals, UpdatedAt = clock.GetUtcNow() }
            : plan with { Status = PlanStatuses.Failed, Error = failure ?? UnavailableText, UpdatedAt = clock.GetUtcNow() };
        await repository.TryReplaceAsync(current, finished, cancellationToken);
        LogGenerated(finished.Status, meals.Count, orphansBefore, PlanOverlap.Orphans(meals).Count, retried, (int)clock.GetElapsedTime(started).TotalMilliseconds);
    }

    public const string BusyText = "Busy right now, try again in a minute.";
    public const string UnavailableText = "Couldn't plan just now. Try again.";

    // ---- Read

    public async Task<MealPlanView?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await repository.GetAsync(id, cancellationToken) is { } current ? ToView(current.Value) : null;

    /// <summary>Delete a plan. Idempotent: a plan that's gone is still a success.</summary>
    public async Task DeletePlanAsync(Guid id, CancellationToken cancellationToken)
    {
        await repository.DeleteAsync(id, cancellationToken);
        LogPlanDeleted();
    }

    // ---- Change one meal or the whole plan (each is one revise call, 2 to 9 s)

    public Task<PlanOutcome> AdjustMealAsync(Guid id, string mealId, string? comments, CurrentUser user, CancellationToken cancellationToken) =>
        ChangeAsync(id, user, comments, requireComments: true, (plan, world, wish) =>
        {
            if (plan.Meals.FirstOrDefault(m => m.Id == mealId) is not { } meal)
            {
                return null;
            }

            return new Change(
                $"Adjust meal {mealId}: change it as the household asks (see their preferences). It stays a {meal.Type}.",
                wish, KeepAllBut(plan, mealId), (drafts, current) => Replace(current, drafts, mealId, world, plan.Choices), mealId);
        }, cancellationToken);

    public Task<PlanOutcome> AnotherAsync(Guid id, string mealId, CurrentUser user, CancellationToken cancellationToken) =>
        ChangeAsync(id, user, null, requireComments: false, (plan, world, _) =>
        {
            if (plan.Meals.FirstOrDefault(m => m.Id == mealId) is not { } meal)
            {
                return null;
            }

            var stranded = PlanOverlap.Stranded(plan.Meals, mealId);
            var avoid = plan.Meals.Select(m => m.Title).ToList();
            var instruction = $"Replace meal {mealId} with a different {meal.Type}, {PlanAngles.Pick(Random.Shared)}."
                + (stranded.Count > 0
                    ? $" The other meals buy {string.Join(", ", stranded)} in sharing with it: use those in the new meal so they stay shared."
                    : "")
                + " Avoid these titles: " + string.Join("; ", avoid) + ".";
            return new Change(instruction, null, KeepAllBut(plan, mealId), (drafts, current) => Replace(current, drafts, mealId, world, plan.Choices), mealId);
        }, cancellationToken);

    public Task<PlanOutcome> AddMealAsync(Guid id, string type, CurrentUser user, CancellationToken cancellationToken) =>
        ChangeAsync(id, user, null, requireComments: false, (plan, world, _) =>
        {
            if (!PlanMealTypes.IsValid(type) || plan.Meals.Count >= MaxMeals)
            {
                return null;
            }

            var newId = NewId(plan.Meals, type);
            var avoid = plan.Meals.Where(m => m.Type == type).Select(m => m.Title).ToList();
            var instruction = $"Add one more {type}, with id {newId}, {PlanAngles.Pick(Random.Shared)}."
                + (avoid.Count > 0 ? " Avoid these titles: " + string.Join("; ", avoid) + "." : "");
            return new Change(instruction, null, plan.Meals.Select(m => m.Id).ToHashSet(), (drafts, current) =>
            {
                if (drafts.Count == 0)
                {
                    return null;
                }

                var draft = drafts[0];
                return [.. current, ToMeal(draft with { Id = newId, Type = type }, newId, world, plan.Choices)];
            }, newId, AddType: type);
        }, cancellationToken);

    public Task<PlanOutcome> AdjustPlanAsync(Guid id, string? comments, CurrentUser user, CancellationToken cancellationToken) =>
        ChangeAsync(id, user, comments, requireComments: true, (plan, world, wish) =>
            new Change(
                "Change the whole plan as the household asks (see their preferences). Meals marked KEEP stay exactly as they are; rewrite the others, keeping the plan's sharing of ingredients.",
                wish, plan.Meals.Where(m => m.Keep).Select(m => m.Id).ToHashSet(),
                (drafts, current) => MergeAll(current, drafts, plan.Meals.Where(m => m.Keep).Select(m => m.Id).ToHashSet(), world, plan.Choices), null),
            cancellationToken);

    // ---- Changes that need no model

    public async Task<PlanOutcome> DeleteMealAsync(Guid id, string mealId, CancellationToken cancellationToken) =>
        await UpdateAsync(id, plan =>
            plan.Meals.FirstOrDefault(m => m.Id == mealId) is { } meal
                ? (plan with { Meals = [.. plan.Meals.Where(m => m.Id != mealId)] }, meal)
                : null, deleted: true, cancellationToken);

    /// <summary>Undo of a delete: puts the meal back (idempotent: a meal already there is left as it is).</summary>
    public async Task<PlanOutcome> RestoreMealAsync(Guid id, PlannedMeal meal, CancellationToken cancellationToken)
    {
        if (!PlanMealTypes.IsValid(meal.Type) || string.IsNullOrWhiteSpace(meal.Id) || meal.Id.Length > 40 || string.IsNullOrWhiteSpace(meal.Title)
            || meal.Title.Length > 120 || meal.Summary.Length > 400 || meal.ToBuy.Count > MaxToBuyPerMeal || meal.Uses.Count > 40)
        {
            return new PlanOutcome.Invalid("That meal isn't in the right shape.");
        }

        return await UpdateAsync(id, plan =>
            plan.Meals.Any(m => m.Id == meal.Id) || plan.Meals.Count >= MaxMeals ? (plan, meal) : (plan with { Meals = [.. plan.Meals, meal] }, meal),
            deleted: false, cancellationToken);
    }

    /// <summary>Keep (or stop keeping) a meal: a kept meal survives "Adjust the whole plan", with its recipe.</summary>
    public async Task<PlanOutcome> KeepAsync(Guid id, string mealId, bool keep, CancellationToken cancellationToken) =>
        await UpdateAsync(id, plan =>
            plan.Meals.Any(m => m.Id == mealId)
                ? (plan with { Meals = [.. plan.Meals.Select(m => m.Id == mealId ? m with { Keep = keep } : m)] }, null)
                : null, deleted: false, cancellationToken);

    // ---- Internals

    private sealed record Change(
        string Instruction, string? Comments, IReadOnlySet<string> Keep,
        Func<IReadOnlyList<DraftMeal>, IReadOnlyList<PlannedMeal>, IReadOnlyList<PlannedMeal>?> Apply, string? DraftsFor, string? AddType = null);

    private async Task<PlanOutcome> ChangeAsync(
        Guid id, CurrentUser user, string? comments, bool requireComments,
        Func<MealPlan, PlanWorld, string?, Change?> build, CancellationToken cancellationToken)
    {
        var wish = string.IsNullOrWhiteSpace(comments) ? null : comments.Trim();
        if (requireComments && wish is null)
        {
            return new PlanOutcome.Invalid("Say what to change.");
        }

        if (wish?.Length > MaxCommentsLength)
        {
            return new PlanOutcome.Invalid($"Comments can be at most {MaxCommentsLength} characters.");
        }

        if (await repository.GetAsync(id, cancellationToken) is not { } current)
        {
            return new PlanOutcome.NotFound("No such plan.");
        }

        var plan = current.Value;
        if (plan.Status != PlanStatuses.Ready)
        {
            return new PlanOutcome.NotReady(plan.Status == PlanStatuses.Failed ? "That plan couldn't be made." : "That plan is still being made.");
        }

        if (await BuildWorldAsync(plan.Choices, cancellationToken) is not { } world)
        {
            return new PlanOutcome.NothingInStock();
        }

        if (build(plan, world, wish) is not { } change)
        {
            return new PlanOutcome.NotFound("No such meal.");
        }

        if (!limit.TryTake(user.ObjectId))
        {
            LogRateLimited();
            return new PlanOutcome.TooManyRequests();
        }

        var counts = Counts(plan.Meals, change.AddType);
        var request = new ReviseRequest(world.Context, counts, [.. plan.Meals.Select(m => new CurrentMeal(ToDraft(m), change.Keep.Contains(m.Id)))],
            change.Instruction, change.Comments);
        var result = await planner.ReviseAsync(request, cancellationToken);
        if (result is not PlannerResult.Drafted drafted)
        {
            LogChangeFailed(result is PlannerResult.Busy);
            return result is PlannerResult.Busy ? new PlanOutcome.Busy() : new PlanOutcome.Unavailable();
        }

        if (change.Apply(drafted.Meals, plan.Meals) is not { } meals)
        {
            LogChangeFailed(false);
            return new PlanOutcome.Unavailable();
        }

        var updated = plan with { Meals = meals, UpdatedAt = clock.GetUtcNow() };
        if (!await repository.TryReplaceAsync(current, updated, cancellationToken))
        {
            return new PlanOutcome.Conflict();
        }

        LogChanged(meals.Count, PlanOverlap.Orphans(meals).Count);
        return new PlanOutcome.Done(ToView(updated));
    }

    private async Task<PlanOutcome> UpdateAsync(
        Guid id, Func<MealPlan, (MealPlan Updated, PlannedMeal? Meal)?> change, bool deleted, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(id, cancellationToken) is not { } current)
        {
            return new PlanOutcome.NotFound("No such plan.");
        }

        if (change(current.Value) is not { } result)
        {
            return new PlanOutcome.NotFound("No such meal.");
        }

        var updated = result.Updated with { UpdatedAt = ReferenceEquals(result.Updated, current.Value) ? current.Value.UpdatedAt : clock.GetUtcNow() };
        if (!ReferenceEquals(result.Updated, current.Value) && !await repository.TryReplaceAsync(current, updated, cancellationToken))
        {
            return new PlanOutcome.Conflict();
        }

        return deleted && result.Meal is { } meal ? new PlanOutcome.MealDeleted(ToView(updated), meal) : new PlanOutcome.Done(ToView(updated));
    }

    private static HashSet<string> KeepAllBut(MealPlan plan, string mealId) => [.. plan.Meals.Where(m => m.Id != mealId).Select(m => m.Id)];

    private static string NewId(IReadOnlyList<PlannedMeal> meals, string type)
    {
        for (var n = meals.Count(m => m.Type == type) + 1; ; n++)
        {
            var id = $"{type}-{n}";
            if (meals.All(m => m.Id != id))
            {
                return id;
            }
        }
    }

    private static Dictionary<string, int> Counts(PlanChoices choices) =>
        PlanMealTypes.All.Where(choices.MealTypes.Contains).ToDictionary(t => t, choices.Count);

    private static Dictionary<string, int> Counts(IReadOnlyList<PlannedMeal> meals, string? addType = null) =>
        PlanMealTypes.All.Select(t => (Type: t, Count: meals.Count(m => m.Type == t) + (t == addType ? 1 : 0))).Where(x => x.Count > 0)
            .ToDictionary(x => x.Type, x => x.Count);

    public static string? Validate(PlanChoices choices)
    {
        if (choices.Days is < 1 or > MaxDays)
        {
            return $"Plan 1 to {MaxDays} days.";
        }

        if (choices.MealTypes.Count == 0 || choices.MealTypes.Any(t => !PlanMealTypes.IsValid(t)) || choices.MealTypes.Distinct().Count() != choices.MealTypes.Count)
        {
            return "Pick at least one of breakfast, lunch and dinner.";
        }

        if (Diets.Parse(choices.Diet) is null)
        {
            return "Diet must be any, veg or half.";
        }

        if (choices.Equipment?.Any(k => Kits.Parse(k) is null) == true)
        {
            return "Unknown kit.";
        }

        return choices.Comments?.Trim().Length > MaxCommentsLength ? $"Comments can be at most {MaxCommentsLength} characters." : null;
    }

    private sealed record PlanWorld(
        PlanContext Context, IReadOnlyDictionary<Guid, StockProduct> ById, IReadOnlySet<Kit> AllowedKit, IReadOnlyDictionary<Guid, PlanStockItem> Items);

    /// <summary>The stock and "usually buys", read now. Null when nothing is in stock. History is best effort.</summary>
    private async Task<PlanWorld?> BuildWorldAsync(PlanChoices choices, CancellationToken cancellationToken)
    {
        var view = await stock.ExecuteAsync(cancellationToken);
        var known = view.Aisles.SelectMany(a => a.Products.Select(p => (Aisle: a, Product: p))).ToList();
        if (known.Count == 0)
        {
            return null;
        }

        IReadOnlyList<string> usual = [];
        try
        {
            var (units, memory) = await history.GetAsync(usuallyBuys.Start, cancellationToken);
            usual = UsuallyBuys.Rank(units, memory, usuallyBuys);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogHistoryFailed(e);
        }

        var items = known.Select(k => new RecipeStockItem(
            k.Product.ProductId, k.Product.DisplayName, k.Product.Count, k.Product.DaysUntilSoonest, k.Product.UseFirst, k.Aisle.Name, k.Product.OpenedCount,
            k.Product.AnimalContent)).ToList();
        var kit = choices.Equipment is null ? Kits.All : choices.Equipment.Select(k => Kits.Parse(k)!.Value).ToHashSet();
        return new PlanWorld(
            new PlanContext(choices, items, usual),
            known.ToDictionary(k => k.Product.ProductId, k => k.Product),
            kit,
            known.ToDictionary(k => k.Product.ProductId, k => new PlanStockItem(
                k.Product.ProductId, k.Product.DisplayName, (k.Product.GenericName ?? k.Product.DisplayName).ToLowerInvariant(), k.Product.Count, k.Aisle.AisleId,
                k.Product.UseFirst)));
    }

    private static DraftMeal ToDraft(PlannedMeal m) =>
        new(m.Id, m.Type, m.Title, m.Summary, m.Servings, [.. m.Uses.Select(u => u.ProductId)], [.. m.UsesUp.Select(u => u.ProductId)], m.ToBuy);

    /// <summary>Drafts to meals: ids by code, the model's counts trimmed to what was asked, in plan order.</summary>
    private static List<PlannedMeal> Assemble(
        IReadOnlyList<DraftMeal> drafts, Dictionary<string, int> counts, PlanWorld world, PlanChoices choices)
    {
        var meals = new List<PlannedMeal>();
        foreach (var type in PlanMealTypes.All.Where(counts.ContainsKey))
        {
            var n = 0;
            foreach (var draft in drafts.Where(d => d.Type == type).Take(counts[type]))
            {
                n++;
                meals.Add(ToMeal(draft, $"{type}-{n}", world, choices));
            }
        }

        return meals;
    }

    private static PlannedMeal ToMeal(DraftMeal draft, string id, PlanWorld world, PlanChoices choices)
    {
        PlannedProduct[] Known(IEnumerable<Guid> ids) =>
            [.. ids.Distinct().Where(world.ById.ContainsKey).Select(i => new PlannedProduct(i, world.ById[i].DisplayName))];
        var uses = Known(draft.Uses);
        var usesUp = choices.PreferUseFirst ? Known(draft.UsesUp).Where(u => world.ById[u.ProductId].UseFirst).ToArray() : [];
        var inStock = world.ById.Values.SelectMany(p => new[] { PlanOverlap.Normalise(p.DisplayName), PlanOverlap.Normalise(p.GenericName ?? p.DisplayName) }).ToHashSet();
        var toBuy = draft.ToBuy.Select(b => b.Trim()).Where(b => b.Length is > 0 and <= 60 && !inStock.Contains(PlanOverlap.Normalise(b)))
            .DistinctBy(PlanOverlap.Normalise).Take(MaxToBuyPerMeal).ToArray();
        var title = Trim(draft.Title, 120);
        var summary = Trim(draft.Summary, 400);
        return new PlannedMeal(id, draft.Type, title, summary, Math.Clamp(draft.Servings, 1, 12), uses, usesUp, toBuy, false,
            WarningsFor(title, summary, uses, toBuy, world, choices));
    }

    private static string Trim(string text, int max) => text.Trim() is { } t && t.Length > max ? t[..max].TrimEnd() : text.Trim();

    private static string[] WarningsFor(
        string title, string summary, IReadOnlyList<PlannedProduct> uses, IReadOnlyList<string> toBuy, PlanWorld world, PlanChoices choices)
    {
        var warnings = new List<string>();
        if (choices.Diet == "veg")
        {
            foreach (var u in uses.Where(u => world.ById[u.ProductId].AnimalContent is AnimalContent.Meat or AnimalContent.Fish))
            {
                warnings.Add($"Uses {u.Name}, which is meat or fish.");
            }

            if (DietChecks.MeatWordIn($"{title}. {summary}. {string.Join(", ", toBuy)}") is { } word)
            {
                warnings.Add($"This meal mentions {word}.");
            }
        }

        foreach (var kit in Kits.NotAllowed([title, summary], [], world.AllowedKit))
        {
            warnings.Add(Kits.Warning(kit).Replace("This recipe", "This meal", StringComparison.Ordinal));
        }

        return [.. warnings.Distinct()];
    }

    /// <summary>Replaces one meal with the first draft (its id forced), keeping everything else as it was.</summary>
    private static IReadOnlyList<PlannedMeal>? Replace(
        IReadOnlyList<PlannedMeal> current, IReadOnlyList<DraftMeal> drafts, string mealId, PlanWorld world, PlanChoices choices)
    {
        var draft = drafts.FirstOrDefault(d => d.Id == mealId) ?? (drafts.Count > 0 ? drafts[0] : null);
        if (draft is null)
        {
            return null;
        }

        var type = current.First(m => m.Id == mealId).Type;
        var meal = ToMeal(draft with { Type = type }, mealId, world, choices);
        return [.. current.Select(m => m.Id == mealId ? meal : m)];
    }

    /// <summary>Applies a whole-plan revision: kept meals never change (code's rule, not the model's), changed ones are replaced
    /// (their recipe, if any, is cleared), new ones are added.</summary>
    private static List<PlannedMeal>? MergeAll(
        IReadOnlyList<PlannedMeal> current, IReadOnlyList<DraftMeal> drafts, HashSet<string> keep, PlanWorld world, PlanChoices choices)
    {
        var meals = current.ToList();
        var changed = 0;
        foreach (var draft in drafts.Where(d => !keep.Contains(d.Id) && PlanMealTypes.IsValid(d.Type)))
        {
            var at = meals.FindIndex(m => m.Id == draft.Id);
            var id = at >= 0 ? draft.Id : NewId(meals, draft.Type);
            var meal = ToMeal(draft, id, world, choices);
            if (at >= 0)
            {
                meals[at] = meal;
            }
            else if (meals.Count < MaxMeals)
            {
                meals.Add(meal);
            }

            changed++;
        }

        return changed == 0 ? null : meals;
    }

    /// <summary>One retry (spike #124): the things bought for only one meal are named, and the model is asked to share or replace
    /// them. Best effort: if it fails, the plan stands as it was.</summary>
    private async Task<IReadOnlyList<PlannedMeal>> ShareRetryAsync(
        PlanChoices choices, PlanWorld world, IReadOnlyList<PlannedMeal> meals, CancellationToken cancellationToken)
    {
        var orphans = PlanOverlap.Orphans(meals).Select(o => o.Name).ToList();
        var request = new ReviseRequest(
            world.Context, Counts(meals), [.. meals.Select(m => new CurrentMeal(ToDraft(m), false))],
            "These items are each bought for only ONE meal: " + string.Join(", ", orphans)
            + ". Revise the plan so each is shared with another meal or replaced by something in stock or already shared. Return the changed meals.",
            null);
        if (await planner.ReviseAsync(request, cancellationToken) is not PlannerResult.Drafted drafted)
        {
            return meals;
        }

        var merged = MergeAll(meals, drafted.Meals, new HashSet<string>(), world, choices);
        return merged is not null && merged.Count == meals.Count ? merged : meals;
    }

    // ---- The view

    public static MealPlanView ToView(MealPlan plan) =>
        new(plan, [.. plan.Meals.Select(m => new PlannedMealView(
            m, m.Uses.Count, PlanOverlap.SharesOf(plan.Meals, m), plan.Choices.PreferUseFirst ? [.. m.UsesUp.Select(u => u.Name)] : []))]);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Meal plan created: {Days} days, {Meals} meals, diet {Diet}, prefer use first {PreferUseFirst}, comments given {CommentsGiven}.")]
    private partial void LogCreated(int days, int meals, string diet, bool preferUseFirst, bool commentsGiven);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Meal plan written: {Status}, {Meals} meals, {OrphansBefore} bought for one meal only before and {OrphansAfter} after (retried {Retried}), {Ms} ms.")]
    private partial void LogGenerated(string status, int meals, int orphansBefore, int orphansAfter, bool retried, int ms);

    [LoggerMessage(Level = LogLevel.Information, Message = "Meal plan changed: {Meals} meals, {Orphans} bought for one meal only.")]
    private partial void LogChanged(int meals, int orphans);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Meal plan change failed (busy {Busy}); the plan is as it was.")]
    private partial void LogChangeFailed(bool busy);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing a meal plan stopped unexpectedly.")]
    private partial void LogGenerateFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the purchase history for 'usually buys'; planning without it.")]
    private partial void LogHistoryFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plan request refused: over the hourly limit.")]
    private partial void LogRateLimited();

    [LoggerMessage(Level = LogLevel.Information, Message = "Meal plan deleted.")]
    private partial void LogPlanDeleted();
}
