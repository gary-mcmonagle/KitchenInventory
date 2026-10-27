using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.AiModels;

/// <summary>What a Foundry model is used for (#120): each job has its own active model.</summary>
public enum AiJob
{
    /// <summary>Recipes, Ready now, Adjust and idea Adjust.</summary>
    Recipes,

    /// <summary>Photo recognition: needs image input.</summary>
    Photos,

    /// <summary>Generic names.</summary>
    GenericNames,
}

public static class AiJobs
{
    public static readonly IReadOnlyList<AiJob> All = [AiJob.Recipes, AiJob.Photos, AiJob.GenericNames];

    public static string Id(AiJob job) => job switch
    {
        AiJob.Recipes => "recipes",
        AiJob.Photos => "photos",
        _ => "genericNames",
    };

    public static string Words(AiJob job) => job switch
    {
        AiJob.Recipes => "recipes",
        AiJob.Photos => "photos",
        _ => "generic names",
    };

    public static AiJob? Parse(string? id) => All.Cast<AiJob?>().FirstOrDefault(j => AiJobs.Id(j!.Value) == id);
}

/// <summary>Whether a model gets the temperature each caller sends today (<see cref="Usual"/>), or none, because it only
/// accepts its default (<see cref="Default"/>: gpt-6-luna). Set by the test suite (#121); new models start as usual.</summary>
public enum TemperatureMode
{
    Usual,
    Default,
}

public static class TemperatureModes
{
    public static string Id(TemperatureMode mode) => mode == TemperatureMode.Default ? "default" : "usual";

    public static TemperatureMode? Parse(string? id) => id switch
    {
        "usual" => TemperatureMode.Usual,
        "default" => TemperatureMode.Default,
        _ => null,
    };
}

/// <summary>
/// A Foundry model registered with the app (#120). <see cref="Deployment"/> is the deployment name as shown in Foundry
/// (the key in <c>/openai/deployments/&lt;name&gt;/…</c>, not necessarily the model's name). <see cref="Jobs"/> is what
/// it has passed tests for (set by #121; empty until tested), and <see cref="Tests"/> the latest results, kept as given.
/// </summary>
public sealed record AiModel(
    string Id,
    string Label,
    string Deployment,
    TemperatureMode TemperatureMode,
    IReadOnlyList<AiJob> Jobs,
    Guid AddedBy,
    string AddedByName,
    DateTimeOffset AddedAt,
    JsonObject? Tests = null);

/// <summary>A job's active model, who chose it and when, and the one before (null: the job was on its default), for revert.</summary>
public sealed record ActiveSelection(string ModelId, Guid ChangedBy, string ChangedByName, DateTimeOffset ChangedAt, string? PreviousModelId);

/// <summary>The household's registered models and each job's active one. No document at all means every job is on its
/// default (the app settings, then gpt-4o).</summary>
public sealed record AiModelSettings(IReadOnlyList<AiModel> Models, IReadOnlyDictionary<AiJob, ActiveSelection> Active)
{
    public static readonly AiModelSettings Empty = new([], new Dictionary<AiJob, ActiveSelection>());

    public AiModel? Find(string id) => Models.FirstOrDefault(m => m.Id == id);
}

/// <summary>From app settings: each job's default deployment (RECIPE_MODEL_DEPLOYMENT and so on), used until a model is made active.</summary>
public sealed record AiModelDefaults(string Recipes, string Photos, string GenericNames)
{
    public string For(AiJob job) => job switch
    {
        AiJob.Recipes => Recipes,
        AiJob.Photos => Photos,
        _ => GenericNames,
    };
}

/// <summary>What a caller sends to: the deployment, and whether to send its usual temperature.</summary>
public sealed record ResolvedModel(string Deployment, TemperatureMode TemperatureMode)
{
    /// <summary>The temperature to send: the caller's usual one, or none (null) for a model that only takes its default.</summary>
    public double? Temperature(double usual) => TemperatureMode == TemperatureMode.Default ? null : usual;
}

/// <summary>The one <c>settings/aiModels</c> document, with its ETag. <c>TryCreate</c> and <c>TryReplace</c> are false on a conflict.</summary>
public interface IAiModelSettingsRepository
{
    Task<Versioned<AiModelSettings>?> GetAsync(CancellationToken cancellationToken);

    Task<bool> TryCreateAsync(AiModelSettings settings, CancellationToken cancellationToken);

    Task<bool> TryReplaceAsync(Versioned<AiModelSettings> current, AiModelSettings updated, CancellationToken cancellationToken);
}

/// <summary>The active model for a job, read per request (#120).</summary>
public interface IActiveAiModels
{
    /// <summary>The job's active model, or <paramref name="fallbackDeployment"/> with the usual temperature when none is
    /// chosen or the settings can't be read (a settings problem never stops a recipe).</summary>
    Task<ResolvedModel> ForAsync(AiJob job, string fallbackDeployment, CancellationToken cancellationToken);

    /// <summary>Forget what was read, so the next call reads again (after a write on this instance).</summary>
    void Invalidate();
}

/// <summary>
/// Reads the settings document at most once a minute per app instance (like the shopping list's suggestion index), so a
/// change by someone takes effect within about a minute everywhere, and at once on the instance that made it. A failed
/// read keeps the last good copy (or none: the defaults) and tries again after 15 seconds.
/// </summary>
public sealed partial class ActiveAiModels(IAiModelSettingsRepository repository, TimeProvider clock, ILogger<ActiveAiModels> logger)
    : IActiveAiModels, IDisposable
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim gate = new(1, 1);
    private AiModelSettings? settings;
    private DateTimeOffset validUntil = DateTimeOffset.MinValue;

    public async Task<ResolvedModel> ForAsync(AiJob job, string fallbackDeployment, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(cancellationToken);
        if (current.Active.TryGetValue(job, out var selection) && current.Find(selection.ModelId) is { } model)
        {
            return new ResolvedModel(model.Deployment, model.TemperatureMode);
        }

        return new ResolvedModel(fallbackDeployment, TemperatureMode.Usual);
    }

    public void Invalidate() => validUntil = DateTimeOffset.MinValue;

    public void Dispose() => gate.Dispose();

    private async Task<AiModelSettings> ReadAsync(CancellationToken cancellationToken)
    {
        if (clock.GetUtcNow() < validUntil && settings is not null)
        {
            return settings;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (clock.GetUtcNow() < validUntil && settings is not null)
            {
                return settings;
            }

            try
            {
                settings = (await repository.GetAsync(cancellationToken))?.Value ?? AiModelSettings.Empty;
                validUntil = clock.GetUtcNow() + CacheFor;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogReadFailed(e);
                settings ??= AiModelSettings.Empty;
                validUntil = clock.GetUtcNow() + RetryAfterFailure;
            }

            return settings;
        }
        finally
        {
            gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the AI model settings; using the last good copy, or the defaults.")]
    private partial void LogReadFailed(Exception exception);
}
