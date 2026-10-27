using CupboardTracker.Application.AiModels;
using CupboardTracker.Application.Users;

namespace CupboardTracker.Application.Tests.AiModels;

internal sealed class FakeAiModelSettings : IAiModelSettingsRepository
{
    private int version;

    public AiModelSettings? Stored { get; set; }

    public int Reads { get; private set; }

    /// <summary>Fail the next N writes as if someone else wrote first.</summary>
    public int ConflictsToGive { get; set; }

    public Exception? FailReads { get; set; }

    public Task<Versioned<AiModelSettings>?> GetAsync(CancellationToken cancellationToken)
    {
        Reads++;
        if (FailReads is not null)
        {
            throw FailReads;
        }

        return Task.FromResult(Stored is null ? null : new Versioned<AiModelSettings>(Stored, $"v{version}"));
    }

    public Task<bool> TryCreateAsync(AiModelSettings settings, CancellationToken cancellationToken)
    {
        if (Stored is not null || ConflictsToGive-- > 0)
        {
            return Task.FromResult(false);
        }

        Stored = settings;
        version++;
        return Task.FromResult(true);
    }

    public Task<bool> TryReplaceAsync(Versioned<AiModelSettings> current, AiModelSettings updated, CancellationToken cancellationToken)
    {
        if (current.ETag != $"v{version}" || ConflictsToGive-- > 0)
        {
            return Task.FromResult(false);
        }

        Stored = updated;
        version++;
        return Task.FromResult(true);
    }
}

// Foundry model settings (#120): registering models, choosing the active one per job, reverting, and reading it per request.
public sealed class AiModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Gary = new(Guid.Parse("caf87c93-9030-44e8-a25d-fed63261ea67"), "Gary");
    private static readonly CurrentUser Sam = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Sam");

    private readonly FixedClock clock = new(Now);
    private readonly FakeAiModelSettings repository = new();
    private readonly ActiveAiModels active;
    private readonly ManageAiModels manage;

    public AiModelTests()
    {
        active = new ActiveAiModels(repository, clock, new ListLogger<ActiveAiModels>());
        manage = new ManageAiModels(repository, active, clock, new ListLogger<ManageAiModels>());
    }

    public void Dispose() => active.Dispose();

    private static async Task<AiModelSettings> Done(Task<AiModelsResult> result) => Assert.IsType<AiModelsResult.Done>(await result).Settings;

    private Task<AiModelsResult> Add(string id, string deployment, string? label = null) =>
        manage.PutModelAsync(id, label ?? id.ToUpperInvariant(), deployment, Gary, CancellationToken.None);

    /// <summary>What #121's tests will do: a model has passed these jobs, and takes its default temperature.</summary>
    private void Passed(string id, TemperatureMode mode, params AiJob[] jobs) =>
        repository.Stored = repository.Stored! with
        {
            Models = [.. repository.Stored.Models.Select(m => m.Id == id ? m with { Jobs = jobs, TemperatureMode = mode } : m)],
        };

    // ---- Registering

    [Fact]
    public async Task A_new_model_starts_untested_on_the_usual_temperature_with_who_and_when()
    {
        var settings = await Done(Add("luna", "gpt-6-luna", "GPT-6 Luna"));

        var model = Assert.Single(settings.Models);
        Assert.Equal(("luna", "GPT-6 Luna", "gpt-6-luna", TemperatureMode.Usual), (model.Id, model.Label, model.Deployment, model.TemperatureMode));
        Assert.Empty(model.Jobs);
        Assert.Equal((Gary.ObjectId, "Gary", Now), (model.AddedBy, model.AddedByName, model.AddedAt));
        Assert.Empty(settings.Active);
    }

    [Theory]
    [InlineData("", "gpt-4o", "The label")]
    [InlineData("  ", "gpt-4o", "The label")]
    [InlineData("A model", "", "The deployment name")]
    [InlineData("A model", "has space", "The deployment name")]
    [InlineData("A model", "a/b", "The deployment name")]
    [InlineData("A model", "a?api-version=1", "The deployment name")]
    public async Task A_bad_label_or_deployment_name_is_refused(string label, string deployment, string startsWith)
    {
        var result = await manage.PutModelAsync("m", label, deployment, Gary, CancellationToken.None);

        Assert.StartsWith(startsWith, Assert.IsType<AiModelsResult.Invalid>(result).Error, StringComparison.Ordinal);
        Assert.Null(repository.Stored);
    }

    [Fact]
    public async Task Limits_on_lengths_and_the_id()
    {
        Assert.IsType<AiModelsResult.Invalid>(await manage.PutModelAsync("m", new string('x', 61), "d", Gary, CancellationToken.None));
        Assert.IsType<AiModelsResult.Invalid>(await manage.PutModelAsync("m", "ok", new string('d', 65), Gary, CancellationToken.None));
        Assert.IsType<AiModelsResult.Invalid>(await manage.PutModelAsync("not valid!", "ok", "d", Gary, CancellationToken.None));
        Assert.IsType<AiModelsResult.Done>(await manage.PutModelAsync("m", new string('x', 60), new string('d', 64), Gary, CancellationToken.None));
    }

    [Fact]
    public async Task The_same_deployment_cant_be_registered_twice_even_in_another_case()
    {
        await Done(Add("luna", "gpt-6-luna", "Luna"));

        var result = await Add("other", "GPT-6-LUNA");

        Assert.Equal("That deployment is already registered as Luna.", Assert.IsType<AiModelsResult.Conflict>(result).Reason);
    }

    [Fact]
    public async Task At_most_twenty_models()
    {
        for (var i = 0; i < ManageAiModels.MaxModels; i++)
        {
            await Done(Add($"m{i}", $"dep-{i}"));
        }

        Assert.IsType<AiModelsResult.Conflict>(await Add("one-more", "dep-extra"));
        Assert.Equal(20, repository.Stored!.Models.Count);
    }

    [Fact]
    public async Task A_registered_model_can_be_renamed_but_its_deployment_cant_change()
    {
        await Done(Add("luna", "gpt-6-luna", "Luna"));
        var before = repository.Stored;

        var renamed = await Done(manage.PutModelAsync("luna", "GPT-6 Luna", "gpt-6-luna", Sam, CancellationToken.None));
        Assert.Equal(("GPT-6 Luna", Gary.ObjectId), (renamed.Models[0].Label, renamed.Models[0].AddedBy)); // who added it stays

        Assert.IsType<AiModelsResult.Invalid>(await manage.PutModelAsync("luna", "GPT-6 Luna", "gpt-6-sol", Sam, CancellationToken.None));
        Assert.NotSame(before, repository.Stored);

        var unchanged = repository.Stored;
        await Done(manage.PutModelAsync("luna", "GPT-6 Luna", "gpt-6-luna", Sam, CancellationToken.None)); // nothing to change
        Assert.Same(unchanged, repository.Stored);
    }

    // ---- Choosing the active model

    [Fact]
    public async Task A_model_can_only_be_made_active_for_a_job_it_passed()
    {
        await Done(Add("luna", "gpt-6-luna", "Luna"));

        var untested = await manage.SetActiveAsync(AiJob.Recipes, "luna", Gary, CancellationToken.None);
        Assert.Equal("Luna hasn't passed the tests for recipes.", Assert.IsType<AiModelsResult.Conflict>(untested).Reason);

        Passed("luna", TemperatureMode.Default, AiJob.Recipes);
        Assert.IsType<AiModelsResult.Conflict>(await manage.SetActiveAsync(AiJob.Photos, "luna", Gary, CancellationToken.None));
        var settings = await Done(manage.SetActiveAsync(AiJob.Recipes, "luna", Sam, CancellationToken.None));

        var selection = settings.Active[AiJob.Recipes];
        Assert.Equal(("luna", Sam.ObjectId, "Sam", Now, (string?)null), (selection.ModelId, selection.ChangedBy, selection.ChangedByName, selection.ChangedAt, selection.PreviousModelId));
        Assert.IsType<AiModelsResult.NotFound>(await manage.SetActiveAsync(AiJob.Recipes, "nope", Gary, CancellationToken.None));
    }

    [Fact]
    public async Task Each_job_has_its_own_active_model()
    {
        await Done(Add("a", "dep-a"));
        await Done(Add("b", "dep-b"));
        Passed("a", TemperatureMode.Usual, AiJob.Recipes, AiJob.Photos);
        Passed("b", TemperatureMode.Default, AiJob.Recipes);
        await Done(manage.SetActiveAsync(AiJob.Recipes, "b", Gary, CancellationToken.None));
        await Done(manage.SetActiveAsync(AiJob.Photos, "a", Gary, CancellationToken.None));

        Assert.Equal("dep-b", (await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None)).Deployment);
        Assert.Equal("dep-a", (await active.ForAsync(AiJob.Photos, "gpt-4o", CancellationToken.None)).Deployment);
        Assert.Equal(new ResolvedModel("gpt-4o", TemperatureMode.Usual), await active.ForAsync(AiJob.GenericNames, "gpt-4o", CancellationToken.None));
    }

    [Fact]
    public async Task Choosing_the_same_model_again_changes_nothing()
    {
        await Done(Add("luna", "gpt-6-luna"));
        Passed("luna", TemperatureMode.Default, AiJob.Recipes);
        await Done(manage.SetActiveAsync(AiJob.Recipes, "luna", Gary, CancellationToken.None));
        var stored = repository.Stored;

        await Done(manage.SetActiveAsync(AiJob.Recipes, "luna", Sam, CancellationToken.None));

        Assert.Same(stored, repository.Stored);
        Assert.Equal("Gary", repository.Stored!.Active[AiJob.Recipes].ChangedByName);
    }

    // ---- Revert

    [Fact]
    public async Task Revert_goes_back_to_the_previous_model_and_reverting_again_undoes_it()
    {
        await Done(Add("a", "dep-a"));
        await Done(Add("b", "dep-b"));
        Passed("a", TemperatureMode.Usual, AiJob.Recipes);
        Passed("b", TemperatureMode.Default, AiJob.Recipes);
        await Done(manage.SetActiveAsync(AiJob.Recipes, "a", Gary, CancellationToken.None));
        await Done(manage.SetActiveAsync(AiJob.Recipes, "b", Gary, CancellationToken.None));

        var back = await Done(manage.RevertAsync(AiJob.Recipes, Sam, CancellationToken.None));
        Assert.Equal(("a", "b", "Sam"), (back.Active[AiJob.Recipes].ModelId, back.Active[AiJob.Recipes].PreviousModelId, back.Active[AiJob.Recipes].ChangedByName));

        var again = await Done(manage.RevertAsync(AiJob.Recipes, Sam, CancellationToken.None));
        Assert.Equal("b", again.Active[AiJob.Recipes].ModelId);
    }

    [Fact]
    public async Task Revert_from_the_first_choice_goes_back_to_the_default()
    {
        await Done(Add("luna", "gpt-6-luna"));
        Passed("luna", TemperatureMode.Default, AiJob.Recipes);
        await Done(manage.SetActiveAsync(AiJob.Recipes, "luna", Gary, CancellationToken.None));

        var settings = await Done(manage.RevertAsync(AiJob.Recipes, Gary, CancellationToken.None));

        Assert.Empty(settings.Active);
        Assert.Equal(new ResolvedModel("gpt-4o", TemperatureMode.Usual), await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None));
        Assert.IsType<AiModelsResult.NotFound>(await manage.RevertAsync(AiJob.Recipes, Gary, CancellationToken.None)); // nothing left to revert
    }

    // ---- Removing

    [Fact]
    public async Task An_active_model_cant_be_removed_but_an_inactive_one_can()
    {
        await Done(Add("a", "dep-a", "Alpha"));
        await Done(Add("b", "dep-b", "Beta"));
        Passed("a", TemperatureMode.Usual, AiJob.Photos);
        await Done(manage.SetActiveAsync(AiJob.Photos, "a", Gary, CancellationToken.None));

        var blocked = await manage.DeleteModelAsync("a", CancellationToken.None);
        Assert.Equal("Alpha is the active model for photos. Choose another one first.", Assert.IsType<AiModelsResult.Conflict>(blocked).Reason);

        var settings = await Done(manage.DeleteModelAsync("b", CancellationToken.None));
        Assert.Equal(["a"], settings.Models.Select(m => m.Id));
        Assert.IsType<AiModelsResult.NotFound>(await manage.DeleteModelAsync("b", CancellationToken.None));
    }

    [Fact]
    public async Task Removing_the_previous_model_forgets_it_so_revert_goes_to_the_default()
    {
        await Done(Add("a", "dep-a"));
        await Done(Add("b", "dep-b"));
        Passed("a", TemperatureMode.Usual, AiJob.Recipes);
        Passed("b", TemperatureMode.Usual, AiJob.Recipes);
        await Done(manage.SetActiveAsync(AiJob.Recipes, "a", Gary, CancellationToken.None));
        await Done(manage.SetActiveAsync(AiJob.Recipes, "b", Gary, CancellationToken.None));

        var settings = await Done(manage.DeleteModelAsync("a", CancellationToken.None));

        Assert.Null(settings.Active[AiJob.Recipes].PreviousModelId);
    }

    // ---- Writes

    [Fact]
    public async Task A_write_that_loses_a_race_is_retried_on_the_newer_settings()
    {
        await Done(Add("a", "dep-a"));
        repository.ConflictsToGive = 2;

        var settings = await Done(Add("b", "dep-b"));

        Assert.Equal(["a", "b"], settings.Models.Select(m => m.Id));
        Assert.Equal(["a", "b"], repository.Stored!.Models.Select(m => m.Id));
    }

    [Fact]
    public async Task Giving_up_after_repeated_conflicts_is_an_error_not_a_silent_loss()
    {
        repository.ConflictsToGive = 100;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Add("a", "dep-a"));
    }

    // ---- Reading per request

    [Fact]
    public async Task With_no_document_every_job_uses_its_default_and_the_usual_temperature()
    {
        foreach (var job in AiJobs.All)
        {
            Assert.Equal(new ResolvedModel("the-default", TemperatureMode.Usual), await active.ForAsync(job, "the-default", CancellationToken.None));
        }
    }

    [Fact]
    public async Task The_active_model_is_read_once_a_minute_and_a_write_on_this_instance_shows_at_once()
    {
        await Done(Add("luna", "gpt-6-luna"));
        Passed("luna", TemperatureMode.Default, AiJob.Recipes);
        await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None);
        var reads = repository.Reads;

        for (var i = 0; i < 20; i++)
        {
            await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None);
        }

        Assert.Equal(reads, repository.Reads); // all from the cache

        await Done(manage.SetActiveAsync(AiJob.Recipes, "luna", Gary, CancellationToken.None));
        Assert.Equal(new ResolvedModel("gpt-6-luna", TemperatureMode.Default), await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None));

        clock.Now += TimeSpan.FromSeconds(59);
        await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None);
        var cachedReads = repository.Reads;
        clock.Now += TimeSpan.FromSeconds(2); // a minute since the last read: another instance's change is picked up
        await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None);
        Assert.Equal(cachedReads + 1, repository.Reads);
    }

    [Fact]
    public async Task A_change_by_someone_else_is_seen_within_a_minute()
    {
        await Done(Add("luna", "gpt-6-luna"));
        Passed("luna", TemperatureMode.Default, AiJob.Recipes);
        Assert.Equal("gpt-4o", (await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None)).Deployment);

        // Another instance writes straight to the store: this instance still has its copy.
        repository.Stored = repository.Stored! with
        {
            Active = new Dictionary<AiJob, ActiveSelection> { [AiJob.Recipes] = new("luna", Sam.ObjectId, "Sam", Now, null) },
        };
        Assert.Equal("gpt-4o", (await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None)).Deployment);

        clock.Now += ActiveAiModels.CacheFor;
        Assert.Equal("gpt-6-luna", (await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None)).Deployment);
    }

    [Fact]
    public async Task An_unreadable_settings_document_never_stops_a_request_and_is_tried_again_soon()
    {
        var logs = new ListLogger<ActiveAiModels>();
        var resolver = new ActiveAiModels(repository, clock, logs);
        repository.FailReads = new HttpRequestException("cosmos down");

        Assert.Equal(new ResolvedModel("gpt-4o", TemperatureMode.Usual), await resolver.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None));
        Assert.Single(logs.At(Microsoft.Extensions.Logging.LogLevel.Warning));
        var reads = repository.Reads;
        await resolver.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None);
        Assert.Equal(reads, repository.Reads); // not hammering a failing store

        repository.FailReads = null;
        await Done(Add("luna", "gpt-6-luna"));
        Passed("luna", TemperatureMode.Default, AiJob.Recipes);
        repository.Stored = repository.Stored! with { Active = new Dictionary<AiJob, ActiveSelection> { [AiJob.Recipes] = new("luna", Gary.ObjectId, "Gary", Now, null) } };
        clock.Now += TimeSpan.FromSeconds(16);
        Assert.Equal("gpt-6-luna", (await resolver.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None)).Deployment);
    }

    [Fact]
    public async Task The_last_good_copy_is_kept_while_the_store_is_down()
    {
        await Done(Add("luna", "gpt-6-luna"));
        Passed("luna", TemperatureMode.Default, AiJob.Recipes);
        await Done(manage.SetActiveAsync(AiJob.Recipes, "luna", Gary, CancellationToken.None));
        Assert.Equal("gpt-6-luna", (await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None)).Deployment);

        repository.FailReads = new HttpRequestException("cosmos down");
        clock.Now += TimeSpan.FromMinutes(5);

        Assert.Equal("gpt-6-luna", (await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None)).Deployment);
    }

    [Fact]
    public async Task An_active_model_that_no_longer_exists_falls_back_to_the_default()
    {
        await Done(Add("luna", "gpt-6-luna"));
        repository.Stored = repository.Stored! with
        {
            Active = new Dictionary<AiJob, ActiveSelection> { [AiJob.Recipes] = new("gone", Gary.ObjectId, "Gary", Now, null) },
        };

        Assert.Equal(new ResolvedModel("gpt-4o", TemperatureMode.Usual), await active.ForAsync(AiJob.Recipes, "gpt-4o", CancellationToken.None));
    }

    [Fact]
    public async Task The_get_use_case_returns_the_stored_settings_or_an_empty_set()
    {
        Assert.Empty((await manage.GetAsync(CancellationToken.None)).Models);

        await Done(Add("a", "dep-a"));

        Assert.Single((await manage.GetAsync(CancellationToken.None)).Models);
    }

    [Fact]
    public void Jobs_have_stable_ids_and_the_defaults_pick_by_job()
    {
        Assert.Equal(["recipes", "photos", "genericNames"], AiJobs.All.Select(AiJobs.Id));
        Assert.Equal(AiJob.GenericNames, AiJobs.Parse("genericNames"));
        Assert.Null(AiJobs.Parse("everything"));
        var defaults = new AiModelDefaults("r", "p", "g");
        Assert.Equal(["r", "p", "g"], AiJobs.All.Select(defaults.For));
        Assert.Equal((TemperatureMode.Default, TemperatureMode.Usual, (TemperatureMode?)null), (TemperatureModes.Parse("default")!.Value, TemperatureModes.Parse("usual")!.Value, TemperatureModes.Parse("hot")));
    }
}
