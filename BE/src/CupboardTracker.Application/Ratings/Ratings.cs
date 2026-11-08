using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Ratings;

/// <summary>What was rated: a Cook something recipe, a Ready now idea, or a planned meal (#126).</summary>
public static class RatingKinds
{
    public const string Recipe = "recipe";
    public const string ReadyNow = "readyNow";
    public const string PlannedMeal = "plannedMeal";

    public static bool IsValid(string? kind) => kind is Recipe or ReadyNow or PlannedMeal;
}

/// <summary>When it was rated: <c>afterCooking</c> is "How was it?" after a planned meal is ticked Made (#126).</summary>
public static class RatingContexts
{
    public const string Suggested = "suggested";
    public const string ReadyNow = "readyNow";
    public const string Saved = "saved";
    public const string PlannedMeal = "plannedMeal";
    public const string AfterCooking = "afterCooking";

    public static bool IsValid(string? context) => context is Suggested or ReadyNow or Saved or PlannedMeal or AfterCooking;
}

public static class RatingValues
{
    public const string Up = "up";
    public const string Down = "down";

    public static bool IsValid(string? value) => value is Up or Down;
}

/// <summary>The optional reasons for a 👎. <c>ingredient</c> goes with the names of the ingredients that weren't liked.</summary>
public static class RatingReasons
{
    public const string TooSpicy = "tooSpicy";
    public const string TooMuchEffort = "tooMuchEffort";
    public const string TookTooLong = "tookTooLong";
    public const string NotFilling = "notFilling";
    public const string Ingredient = "ingredient";
    public const string JustNotForUs = "justNotForUs";

    public static bool IsValid(string? reason) =>
        reason is TooSpicy or TooMuchEffort or TookTooLong or NotFilling or Ingredient or JustNotForUs;
}

/// <summary>A main ingredient in the snapshot of a rated dish: the name as shown, and its generic name when it has one.</summary>
public sealed record RatingIngredient(string Name, string? GenericName);

/// <summary>
/// A small snapshot of what was rated (#128): enough to understand the rating later without a recipe history. No steps
/// and no amounts. <see cref="Equipment"/> holds kit ids; the links are present when there are any.
/// </summary>
public sealed record RatingSnapshot(
    string Kind,
    string Title,
    RecipeMeal Meal,
    Diet Diet,
    int TotalMinutes,
    IReadOnlyList<RatingIngredient> Ingredients,
    IReadOnlyList<string> Equipment,
    Guid? SavedRecipeId = null,
    Guid? PlanId = null,
    string? MealId = null);

/// <summary>
/// One person's 👍 or 👎 of a dish (#128). A person has at most one rating per dish (same kind and title, ignoring case
/// and spaces); other people's ratings of it are separate. <see cref="DislikedIngredients"/> only goes with the
/// <c>ingredient</c> reason.
/// </summary>
public sealed record Rating(
    Guid Id,
    Guid PersonId,
    string PersonName,
    string Value,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> DislikedIngredients,
    string Context,
    DateTimeOffset RatedAt,
    DateTimeOffset UpdatedAt,
    RatingSnapshot Snapshot)
{
    public string NormalizedTitle => RatingKeys.Normalize(Snapshot.Title);
}

public static class RatingKeys
{
    /// <summary>The dish's identity within a kind: trimmed, spaces collapsed, lower case.</summary>
    public static string Normalize(string title) =>
        string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}

/// <summary>The ratings container (#128): one household partition.</summary>
public interface IRatingRepository
{
    Task<Versioned<Rating>?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every rating, without any embedding (written by a later card), in no particular order.</summary>
    Task<IReadOnlyList<Rating>> ListAsync(CancellationToken cancellationToken);

    /// <summary>This person's ratings of the dish (more than one only after a race).</summary>
    Task<IReadOnlyList<Rating>> FindOwnAsync(Guid personId, string kind, string normalizedTitle, CancellationToken cancellationToken);

    /// <summary>Writes the rating, replacing one with the same id.</summary>
    Task UpsertAsync(Rating rating, CancellationToken cancellationToken);

    /// <summary>False if it changed (or went) since it was read.</summary>
    Task<bool> TryReplaceAsync(Versioned<Rating> current, Rating updated, CancellationToken cancellationToken);

    /// <summary>False if there was nothing to delete.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>What the app sends to rate (#128): the rating and a snapshot of what it was looking at.</summary>
public sealed record PutRatingRequest(
    Guid Id,
    string Value,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> DislikedIngredients,
    string Context,
    RatingSnapshot Snapshot);

public abstract record RatingResult
{
    public sealed record Saved(Rating Rating) : RatingResult;

    public sealed record Invalid(string Error) : RatingResult;

    public sealed record NotFound : RatingResult;

    /// <summary>It's someone else's rating.</summary>
    public sealed record Forbidden : RatingResult;

    public sealed record Conflict : RatingResult;
}

/// <summary>One rating on a dish, as Our taste shows it. <see cref="Mine"/> is whether the signed-in person made it.</summary>
public sealed record DishRating(Rating Rating, bool Mine);

/// <summary>A dish and everyone's ratings of it (newest first), as Our taste lists them.</summary>
public sealed record RatedDish(string Kind, string NormalizedTitle, RatingSnapshot Snapshot, DateTimeOffset LatestAt, IReadOnlyList<DishRating> Ratings);

/// <summary>
/// Ratings (#128): rate a dish 👍 or 👎 (idempotent by id; rating the same dish again replaces your earlier rating),
/// change the reasons, take a rating back, list the household's ratings grouped by dish, and record a 👍 when something
/// unrated is saved. You can change or remove only your own. Only metadata is logged: never titles, ingredients or reasons.
/// </summary>
public sealed partial class RatingUseCases(IRatingRepository repository, TimeProvider clock, ILogger<RatingUseCases> logger)
{
    public const int MaxTitle = 80;
    public const int MaxIngredients = 15;
    public const int MaxEquipment = 10;
    public const int MaxName = 100;
    public const int MaxDislikedIngredients = 5;

    public async Task<RatingResult> PutAsync(PutRatingRequest request, CurrentUser user, CancellationToken cancellationToken)
    {
        if (Problem(request) is { } problem)
        {
            return new RatingResult.Invalid(problem);
        }

        if (await repository.GetAsync(request.Id, cancellationToken) is { } taken && taken.Value.PersonId != user.ObjectId)
        {
            return new RatingResult.Forbidden(); // an id is never another person's
        }

        var snapshot = Tidy(request.Snapshot);
        var now = clock.GetUtcNow();
        var reasons = request.Value == RatingValues.Down ? request.Reasons.Distinct().ToList() : [];
        var disliked = request.Value == RatingValues.Down && reasons.Contains(RatingReasons.Ingredient)
            ? request.DislikedIngredients.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxDislikedIngredients).ToList()
            : [];
        var rating = new Rating(request.Id, user.ObjectId, user.DisplayName, request.Value, reasons, disliked, request.Context, now, now, snapshot);

        var earlier = await repository.FindOwnAsync(user.ObjectId, snapshot.Kind, rating.NormalizedTitle, cancellationToken);
        if (earlier.FirstOrDefault(r => r.Id == request.Id) is { } same && Same(same, rating))
        {
            return new RatingResult.Saved(same); // a retry or a repeated tap
        }

        await repository.UpsertAsync(rating, cancellationToken);
        foreach (var other in earlier.Where(r => r.Id != request.Id))
        {
            await repository.DeleteAsync(other.Id, cancellationToken); // one rating per person per dish
        }

        LogRated(rating.Id, rating.Value, rating.Context, rating.Reasons.Count, earlier.Count(r => r.Id != request.Id));
        return new RatingResult.Saved(rating);
    }

    /// <summary>Change the reasons of your own 👎.</summary>
    public async Task<RatingResult> UpdateReasonsAsync(
        Guid id, IReadOnlyList<string> reasons, IReadOnlyList<string> dislikedIngredients, CurrentUser user, CancellationToken cancellationToken)
    {
        if (ReasonsProblem(reasons, dislikedIngredients) is { } problem)
        {
            return new RatingResult.Invalid(problem);
        }

        for (var round = 0; round < 5; round++)
        {
            if (await repository.GetAsync(id, cancellationToken) is not { } current)
            {
                return new RatingResult.NotFound();
            }

            if (current.Value.PersonId != user.ObjectId)
            {
                return new RatingResult.Forbidden();
            }

            if (current.Value.Value != RatingValues.Down)
            {
                return new RatingResult.Invalid("Reasons go with a 👎.");
            }

            var distinct = reasons.Distinct().ToList();
            var updated = current.Value with
            {
                Reasons = distinct,
                DislikedIngredients = distinct.Contains(RatingReasons.Ingredient)
                    ? [.. dislikedIngredients.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxDislikedIngredients)]
                    : [],
                UpdatedAt = clock.GetUtcNow(),
            };
            if (await repository.TryReplaceAsync(current, updated, cancellationToken))
            {
                LogReasons(id, updated.Reasons.Count);
                return new RatingResult.Saved(updated);
            }
        }

        return new RatingResult.Conflict();
    }

    /// <summary>Take back your own rating. Idempotent: one that's already gone is fine.</summary>
    public async Task<RatingResult> DeleteAsync(Guid id, CurrentUser user, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(id, cancellationToken) is not { } current)
        {
            return new RatingResult.NotFound();
        }

        if (current.Value.PersonId != user.ObjectId)
        {
            return new RatingResult.Forbidden();
        }

        if (await repository.DeleteAsync(id, cancellationToken))
        {
            LogRemoved(id);
        }

        return new RatingResult.Saved(current.Value);
    }

    /// <summary>The household's ratings grouped by dish, the dish with the newest rating first.</summary>
    public async Task<IReadOnlyList<RatedDish>> ListAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        var all = await repository.ListAsync(cancellationToken);
        return [.. all
            .GroupBy(r => (r.Snapshot.Kind, r.NormalizedTitle))
            .Select(g =>
            {
                var ratings = g.OrderByDescending(r => r.RatedAt).ThenBy(r => r.Id).ToList();
                return new RatedDish(g.Key.Kind, g.Key.NormalizedTitle, ratings[0].Snapshot, ratings[0].RatedAt,
                    [.. ratings.Select(r => new DishRating(r, r.PersonId == user.ObjectId))]);
            })
            .OrderByDescending(d => d.LatestAt)
            .ThenBy(d => d.NormalizedTitle, StringComparer.Ordinal)];
    }

    /// <summary>
    /// A recipe or idea was saved (#128): record a 👍 for the person unless they've rated that dish already. Saving never
    /// replaces a rating, and unsaving never removes one.
    /// </summary>
    public async Task RecordSavedAsync(RatingSnapshot snapshot, CurrentUser user, CancellationToken cancellationToken)
    {
        var tidy = Tidy(snapshot);
        if ((await repository.FindOwnAsync(user.ObjectId, tidy.Kind, RatingKeys.Normalize(tidy.Title), cancellationToken)).Count > 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var rating = new Rating(Guid.NewGuid(), user.ObjectId, user.DisplayName, RatingValues.Up, [], [], RatingContexts.Saved, now, now, tidy);
        await repository.UpsertAsync(rating, cancellationToken);
        LogRated(rating.Id, rating.Value, rating.Context, 0, 0);
    }

    private static bool Same(Rating a, Rating b) =>
        a.Value == b.Value && a.Context == b.Context && a.Reasons.SequenceEqual(b.Reasons)
        && a.DislikedIngredients.SequenceEqual(b.DislikedIngredients) && a.NormalizedTitle == b.NormalizedTitle && a.Snapshot.Kind == b.Snapshot.Kind;

    private static RatingSnapshot Tidy(RatingSnapshot s) => s with
    {
        Title = Cap(string.Join(' ', s.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), MaxTitle),
        TotalMinutes = Math.Clamp(s.TotalMinutes, 0, 600),
        Ingredients = [.. s.Ingredients.Where(i => !string.IsNullOrWhiteSpace(i.Name)).Take(MaxIngredients)
            .Select(i => new RatingIngredient(Cap(i.Name.Trim(), MaxName), string.IsNullOrWhiteSpace(i.GenericName) ? null : Cap(i.GenericName.Trim(), MaxName)))],
        Equipment = [.. s.Equipment.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => Cap(e.Trim(), 30)).Distinct().Take(MaxEquipment)],
    };

    internal static string? Problem(PutRatingRequest r)
    {
        if (r.Id == Guid.Empty)
        {
            return "Send an id.";
        }

        if (!RatingValues.IsValid(r.Value))
        {
            return "rating must be \"up\" or \"down\".";
        }

        if (!RatingContexts.IsValid(r.Context))
        {
            return "context must be suggested, readyNow, saved, plannedMeal or afterCooking.";
        }

        if (!RatingKinds.IsValid(r.Snapshot.Kind))
        {
            return "kind must be recipe, readyNow or plannedMeal.";
        }

        if (r.Snapshot.Title.Trim().Length is 0 or > MaxTitle)
        {
            return $"A title is 1 to {MaxTitle} characters.";
        }

        if (r.Value == RatingValues.Up && (r.Reasons.Count > 0 || r.DislikedIngredients.Count > 0))
        {
            return "Reasons go with a 👎.";
        }

        return ReasonsProblem(r.Reasons, r.DislikedIngredients);
    }

    private static string? ReasonsProblem(IReadOnlyList<string> reasons, IReadOnlyList<string> dislikedIngredients)
    {
        if (reasons.Any(x => !RatingReasons.IsValid(x)))
        {
            return "Unknown reason.";
        }

        if (reasons.Contains(RatingReasons.Ingredient) && !dislikedIngredients.Any(i => !string.IsNullOrWhiteSpace(i)))
        {
            return "Say which ingredient.";
        }

        if (!reasons.Contains(RatingReasons.Ingredient) && dislikedIngredients.Count > 0)
        {
            return "Ingredients go with the \"ingredient\" reason.";
        }

        if (dislikedIngredients.Count > MaxDislikedIngredients || dislikedIngredients.Any(i => i.Trim().Length > MaxName))
        {
            return $"At most {MaxDislikedIngredients} ingredients, each up to {MaxName} characters.";
        }

        return null;
    }

    private static string Cap(string text, int max) => text.Length > max ? text[..max] : text;

    [LoggerMessage(Level = LogLevel.Information, Message = "Rating {Id} saved ({Value}, {Context}, {Reasons} reasons, {Replaced} earlier replaced).")]
    private partial void LogRated(Guid id, string value, string context, int reasons, int replaced);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rating {Id} reasons changed ({Reasons} reasons).")]
    private partial void LogReasons(Guid id, int reasons);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rating {Id} removed.")]
    private partial void LogRemoved(Guid id);
}
