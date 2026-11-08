using CupboardTracker.Application.Ratings;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests.Ratings;

internal sealed class FakeRatings : IRatingRepository
{
    private int etag;

    public Dictionary<Guid, Versioned<Rating>> Items { get; } = [];

    public Task<Versioned<Rating>?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.GetValueOrDefault(id));

    public Task<IReadOnlyList<Rating>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Rating>>([.. Items.Values.Select(v => v.Value)]);

    public Task<IReadOnlyList<Rating>> FindOwnAsync(Guid personId, string kind, string normalizedTitle, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Rating>>([.. Items.Values.Select(v => v.Value)
            .Where(r => r.PersonId == personId && r.Snapshot.Kind == kind && r.NormalizedTitle == normalizedTitle)]);

    public Task UpsertAsync(Rating rating, CancellationToken cancellationToken)
    {
        Items[rating.Id] = new Versioned<Rating>(rating, $"e{++etag}");
        return Task.CompletedTask;
    }

    public Task<bool> TryReplaceAsync(Versioned<Rating> current, Rating updated, CancellationToken cancellationToken)
    {
        if (Items.GetValueOrDefault(updated.Id)?.ETag != current.ETag)
        {
            return Task.FromResult(false);
        }

        Items[updated.Id] = new Versioned<Rating>(updated, $"e{++etag}");
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.Remove(id));
}

// Ratings (#128): 👍 / 👎 per person per dish.
public sealed class RatingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");
    private static readonly CurrentUser Sam = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Sam");

    private readonly FakeRatings repository = new();
    private readonly FixedClock clock = new(Now);
    private readonly ListLogger<RatingUseCases> logs = new();

    private RatingUseCases UseCases() => new(repository, clock, logs);

    private static RatingSnapshot Snapshot(string title = "Halloumi traybake", string kind = RatingKinds.Recipe, params RatingIngredient[] ingredients) =>
        new(kind, title, RecipeMeal.Dinner, Diet.Any, 40, ingredients.Length > 0 ? ingredients : [new("Halloumi", "Halloumi"), new("Peppers", null)], ["ovenGrill"]);

    private static PutRatingRequest Put(Guid id, string value = RatingValues.Up, RatingSnapshot? snapshot = null, string context = RatingContexts.Suggested,
        string[]? reasons = null, string[]? ingredients = null) =>
        new(id, value, reasons ?? [], ingredients ?? [], context, snapshot ?? Snapshot());

    private static async Task<Rating> Saved(RatingUseCases use, PutRatingRequest request, CurrentUser who) =>
        Assert.IsType<RatingResult.Saved>(await use.PutAsync(request, who, CancellationToken.None)).Rating;

    [Fact]
    public async Task A_rating_is_kept_for_the_person_with_a_snapshot_and_a_retry_changes_nothing()
    {
        var id = Guid.NewGuid();
        var first = await Saved(UseCases(), Put(id), Gary);
        clock.Now = Now.AddMinutes(5);
        var again = await Saved(UseCases(), Put(id), Gary);

        Assert.Equal(("Gary", RatingValues.Up, RatingContexts.Suggested, "halloumi traybake"), (first.PersonName, first.Value, first.Context, first.NormalizedTitle));
        Assert.Equal(first, again); // the same call twice leaves the first one as it was
        Assert.Single(repository.Items);
    }

    [Fact]
    public async Task Rating_the_same_dish_again_replaces_your_earlier_rating()
    {
        var first = await Saved(UseCases(), Put(Guid.NewGuid()), Gary);
        clock.Now = Now.AddDays(1);

        var second = await Saved(UseCases(), Put(Guid.NewGuid(), RatingValues.Down, Snapshot("  HALLOUMI   traybake "), RatingContexts.AfterCooking, [RatingReasons.TookTooLong]), Gary);

        Assert.Single(repository.Items);
        Assert.False(repository.Items.ContainsKey(first.Id));
        Assert.Equal((RatingValues.Down, RatingContexts.AfterCooking, Now.AddDays(1)), (second.Value, second.Context, second.RatedAt));
    }

    [Fact]
    public async Task A_different_kind_is_a_different_dish()
    {
        await Saved(UseCases(), Put(Guid.NewGuid()), Gary);
        await Saved(UseCases(), Put(Guid.NewGuid(), snapshot: Snapshot(kind: RatingKinds.ReadyNow)), Gary);

        Assert.Equal(2, repository.Items.Count);
    }

    [Fact]
    public async Task Each_person_has_their_own_rating_of_a_dish()
    {
        await Saved(UseCases(), Put(Guid.NewGuid()), Gary);
        await Saved(UseCases(), Put(Guid.NewGuid(), RatingValues.Down), Sam);

        var dish = Assert.Single(await UseCases().ListAsync(Gary, CancellationToken.None));
        Assert.Equal(2, dish.Ratings.Count);
        Assert.Equal(["Gary:True:up", "Sam:False:down"], dish.Ratings.OrderBy(r => r.Rating.PersonName).Select(r => $"{r.Rating.PersonName}:{r.Mine}:{r.Rating.Value}"));
    }

    [Fact]
    public async Task An_id_is_never_another_persons()
    {
        var id = Guid.NewGuid();
        await Saved(UseCases(), Put(id), Gary);

        Assert.IsType<RatingResult.Forbidden>(await UseCases().PutAsync(Put(id, RatingValues.Down, Snapshot("Something else")), Sam, CancellationToken.None));
        Assert.Equal(RatingValues.Up, repository.Items[id].Value.Value);
    }

    [Fact]
    public async Task A_down_rating_keeps_its_reasons_and_the_ingredients_that_were_not_liked()
    {
        var rating = await Saved(UseCases(),
            Put(Guid.NewGuid(), RatingValues.Down, reasons: [RatingReasons.Ingredient, RatingReasons.TooSpicy, RatingReasons.TooSpicy], ingredients: ["Peppers", " peppers ", "Halloumi"]), Sam);

        Assert.Equal([RatingReasons.Ingredient, RatingReasons.TooSpicy], rating.Reasons);
        Assert.Equal(["Peppers", "Halloumi"], rating.DislikedIngredients);
    }

    [Theory]
    [InlineData("sideways", "suggested", "recipe", "A title", new string[0], new string[0])]
    [InlineData("up", "never", "recipe", "A title", new string[0], new string[0])]
    [InlineData("up", "suggested", "pizza", "A title", new string[0], new string[0])]
    [InlineData("up", "suggested", "recipe", "   ", new string[0], new string[0])]
    [InlineData("up", "suggested", "recipe", "A title that goes on and on and on and on and on and on and on and on and on and on", new string[0], new string[0])]
    [InlineData("up", "suggested", "recipe", "A title", new[] { "tooSpicy" }, new string[0])] // reasons go with a 👎
    [InlineData("down", "suggested", "recipe", "A title", new[] { "bogus" }, new string[0])]
    [InlineData("down", "suggested", "recipe", "A title", new[] { "ingredient" }, new string[0])] // which one?
    [InlineData("down", "suggested", "recipe", "A title", new[] { "tooSpicy" }, new[] { "Peppers" })]
    public async Task Bad_ratings_are_refused(string value, string context, string kind, string title, string[] reasons, string[] ingredients)
    {
        var result = await UseCases().PutAsync(Put(Guid.NewGuid(), value, Snapshot(title, kind), context, reasons, ingredients), Gary, CancellationToken.None);

        Assert.IsType<RatingResult.Invalid>(result);
        Assert.Empty(repository.Items);
    }

    [Theory]
    [InlineData(RatingContexts.Suggested)]
    [InlineData(RatingContexts.ReadyNow)]
    [InlineData(RatingContexts.Saved)]
    [InlineData(RatingContexts.PlannedMeal)]
    [InlineData(RatingContexts.AfterCooking)]
    public async Task Every_context_is_accepted_and_kept(string context) =>
        Assert.Equal(context, (await Saved(UseCases(), Put(Guid.NewGuid(), context: context), Gary)).Context);

    [Fact]
    public async Task The_snapshot_is_capped_and_has_no_steps_or_amounts()
    {
        var many = Enumerable.Range(1, 30).Select(i => new RatingIngredient($"Ingredient {i}", i % 2 == 0 ? $"Generic {i}" : " ")).ToArray();
        var rating = await Saved(UseCases(), Put(Guid.NewGuid(), snapshot: new RatingSnapshot(
            RatingKinds.Recipe, new string('x', RatingUseCases.MaxTitle), RecipeMeal.Dinner, Diet.Half, 9999, [.. many, new("  ", null)],
            [.. Enumerable.Range(1, 20).Select(i => $"kit{i}")], Guid.NewGuid(), Guid.NewGuid(), "dinner-2")), Gary);

        var s = rating.Snapshot;
        Assert.Equal((RatingUseCases.MaxTitle, RatingUseCases.MaxIngredients, RatingUseCases.MaxEquipment, 600), (s.Title.Length, s.Ingredients.Count, s.Equipment.Count, s.TotalMinutes));
        Assert.Null(s.Ingredients[0].GenericName); // a blank generic name is none
        Assert.Equal("Generic 2", s.Ingredients[1].GenericName);
        Assert.DoesNotContain(typeof(RatingSnapshot).GetProperties(), p => p.Name.Contains("Step") || p.Name.Contains("Amount"));
        Assert.NotNull(s.SavedRecipeId);
        Assert.Equal("dinner-2", s.MealId);
    }

    [Fact]
    public async Task Reasons_can_be_changed_on_your_own_down_rating_only()
    {
        var id = Guid.NewGuid();
        await Saved(UseCases(), Put(id, RatingValues.Down), Sam);

        var changed = await UseCases().UpdateReasonsAsync(id, [RatingReasons.Ingredient, RatingReasons.NotFilling], ["Peppers"], Sam, CancellationToken.None);
        var forbidden = await UseCases().UpdateReasonsAsync(id, [RatingReasons.NotFilling], [], Gary, CancellationToken.None);
        var missing = await UseCases().UpdateReasonsAsync(Guid.NewGuid(), [RatingReasons.NotFilling], [], Sam, CancellationToken.None);

        Assert.Equal([RatingReasons.Ingredient, RatingReasons.NotFilling], Assert.IsType<RatingResult.Saved>(changed).Rating.Reasons);
        Assert.IsType<RatingResult.Forbidden>(forbidden);
        Assert.IsType<RatingResult.NotFound>(missing);
        Assert.Equal(["Peppers"], repository.Items[id].Value.DislikedIngredients);
    }

    [Fact]
    public async Task Reasons_are_refused_on_a_thumbs_up_and_dropping_the_ingredient_reason_drops_the_names()
    {
        var up = Guid.NewGuid();
        var down = Guid.NewGuid();
        await Saved(UseCases(), Put(up), Gary);
        await Saved(UseCases(), Put(down, RatingValues.Down, Snapshot("Another dish"), reasons: [RatingReasons.Ingredient], ingredients: ["Peppers"]), Gary);

        Assert.IsType<RatingResult.Invalid>(await UseCases().UpdateReasonsAsync(up, [RatingReasons.TooSpicy], [], Gary, CancellationToken.None));
        var result = await UseCases().UpdateReasonsAsync(down, [RatingReasons.JustNotForUs], [], Gary, CancellationToken.None);

        Assert.Empty(Assert.IsType<RatingResult.Saved>(result).Rating.DislikedIngredients);
    }

    [Fact]
    public async Task You_can_remove_only_your_own_rating_and_removing_twice_is_fine()
    {
        var id = Guid.NewGuid();
        await Saved(UseCases(), Put(id), Gary);

        Assert.IsType<RatingResult.Forbidden>(await UseCases().DeleteAsync(id, Sam, CancellationToken.None));
        Assert.Single(repository.Items);
        Assert.IsType<RatingResult.Saved>(await UseCases().DeleteAsync(id, Gary, CancellationToken.None));
        Assert.IsType<RatingResult.NotFound>(await UseCases().DeleteAsync(id, Gary, CancellationToken.None));
        Assert.Empty(repository.Items);
    }

    [Fact]
    public async Task Saving_something_you_have_not_rated_records_a_thumbs_up_and_never_replaces_a_rating()
    {
        var use = UseCases();
        var saved = Guid.NewGuid();
        await use.RecordSavedAsync(Snapshot() with { SavedRecipeId = saved }, Gary, CancellationToken.None);
        await use.RecordSavedAsync(Snapshot("Rated already"), Gary, CancellationToken.None);
        await use.RecordSavedAsync(Snapshot(" HALLOUMI traybake"), Gary, CancellationToken.None); // saved again: nothing changes

        var rating = repository.Items.Values.Select(v => v.Value).Single(r => r.NormalizedTitle == "halloumi traybake");
        Assert.Equal((RatingValues.Up, RatingContexts.Saved, saved), (rating.Value, rating.Context, rating.Snapshot.SavedRecipeId));

        await Saved(use, Put(Guid.NewGuid(), RatingValues.Down, Snapshot("Disliked first")), Gary);
        await use.RecordSavedAsync(Snapshot("Disliked first"), Gary, CancellationToken.None);
        Assert.Equal(RatingValues.Down, repository.Items.Values.Select(v => v.Value).Single(r => r.NormalizedTitle == "disliked first").Value);
    }

    [Fact]
    public async Task Saving_records_a_thumbs_up_for_each_person_who_has_not_rated_it()
    {
        await Saved(UseCases(), Put(Guid.NewGuid(), RatingValues.Down), Sam);

        await UseCases().RecordSavedAsync(Snapshot(), Gary, CancellationToken.None);

        Assert.Equal(2, repository.Items.Count);
    }

    [Fact]
    public async Task The_list_groups_by_dish_newest_first_and_a_dish_can_be_in_both_tabs()
    {
        await Saved(UseCases(), Put(Guid.NewGuid(), snapshot: Snapshot("Older dish")), Gary);
        clock.Now = Now.AddHours(1);
        await Saved(UseCases(), Put(Guid.NewGuid(), snapshot: Snapshot("Mixed dish")), Gary);
        clock.Now = Now.AddHours(2);
        await Saved(UseCases(), Put(Guid.NewGuid(), RatingValues.Down, Snapshot("mixed  DISH")), Sam);

        var dishes = await UseCases().ListAsync(Gary, CancellationToken.None);

        Assert.Equal(["mixed dish", "older dish"], dishes.Select(d => d.NormalizedTitle));
        Assert.Equal(["down", "up"], dishes[0].Ratings.Select(r => r.Rating.Value)); // newest rating first
    }

    [Fact]
    public async Task Only_metadata_is_logged()
    {
        var use = UseCases();
        await Saved(use, Put(Guid.NewGuid(), RatingValues.Down, Snapshot("Secret Dish Name"), reasons: [RatingReasons.Ingredient], ingredients: ["Truffle"]), Gary);
        await use.RecordSavedAsync(Snapshot("Another Secret"), Gary, CancellationToken.None);

        var all = string.Join('\n', logs.Entries.Select(e => e.Message));
        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain("Secret", all);
        Assert.DoesNotContain("Truffle", all);
        Assert.DoesNotContain(RatingReasons.Ingredient, all);
        Assert.All(logs.Entries, e => Assert.Equal(LogLevel.Information, e.Level));
    }
}
