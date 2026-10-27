using System.Text.RegularExpressions;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.AiModels;

public abstract record AiModelsResult
{
    /// <summary>It worked (or there was nothing to change): the settings as they are now.</summary>
    public sealed record Done(AiModelSettings Settings) : AiModelsResult;

    /// <summary>400: the request itself is wrong.</summary>
    public sealed record Invalid(string Error) : AiModelsResult;

    /// <summary>404: no such model, or nothing to revert.</summary>
    public sealed record NotFound(string Error) : AiModelsResult;

    /// <summary>409: valid, but not allowed given what's registered.</summary>
    public sealed record Conflict(string Reason) : AiModelsResult;
}

/// <summary>
/// The household's Foundry models (#120): register one by its deployment name, rename it, remove it, choose each job's
/// active model, and revert. Register-only: nothing here creates or lists deployments in Foundry. Any signed-in person
/// may do it; who and when is recorded. Writes are ETag-checked and retried, and clear this instance's cache at once.
/// </summary>
public sealed partial class ManageAiModels(
    IAiModelSettingsRepository repository, IActiveAiModels active, TimeProvider clock, ILogger<ManageAiModels> logger)
{
    public const int MaxModels = 20;
    public const int MaxLabelLength = 60;
    public const int MaxDeploymentLength = 64;

    private const int MaxRounds = 5;

    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,40}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex DeploymentPattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public async Task<AiModelSettings> GetAsync(CancellationToken cancellationToken) =>
        (await repository.GetAsync(cancellationToken))?.Value ?? AiModelSettings.Empty;

    /// <summary>Registers a model (or renames one: its deployment name can't change). New models start untested: no jobs.</summary>
    public Task<AiModelsResult> PutModelAsync(string id, string? label, string? deployment, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!IdPattern.IsMatch(id))
        {
            return Task.FromResult<AiModelsResult>(new AiModelsResult.Invalid("The id must be 1 to 40 letters, digits, - or _."));
        }

        var cleanLabel = label?.Trim() ?? "";
        if (cleanLabel.Length is 0 or > MaxLabelLength || cleanLabel.Any(char.IsControl))
        {
            return Task.FromResult<AiModelsResult>(new AiModelsResult.Invalid($"The label must be 1 to {MaxLabelLength} characters."));
        }

        var cleanDeployment = deployment?.Trim() ?? "";
        if (!DeploymentPattern.IsMatch(cleanDeployment))
        {
            return Task.FromResult<AiModelsResult>(new AiModelsResult.Invalid(
                $"The deployment name must be 1 to {MaxDeploymentLength} characters: letters, digits, '.', '_' and '-', as shown in Foundry."));
        }

        return UpdateAsync(settings =>
        {
            var now = clock.GetUtcNow();
            if (settings.Find(id) is { } existing)
            {
                if (!string.Equals(existing.Deployment, cleanDeployment, StringComparison.Ordinal))
                {
                    return (new AiModelsResult.Invalid("A model's deployment name can't be changed. Add a new model instead."), null);
                }

                return existing.Label == cleanLabel
                    ? (new AiModelsResult.Done(settings), null)
                    : Changed(settings with { Models = [.. settings.Models.Select(m => m.Id == id ? m with { Label = cleanLabel } : m)] });
            }

            if (settings.Models.Count >= MaxModels)
            {
                return (new AiModelsResult.Conflict($"At most {MaxModels} models can be registered. Remove one first."), null);
            }

            if (settings.Models.FirstOrDefault(m => string.Equals(m.Deployment, cleanDeployment, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                return (new AiModelsResult.Conflict($"That deployment is already registered as {same.Label}."), null);
            }

            var added = new AiModel(id, cleanLabel, cleanDeployment, TemperatureMode.Usual, [], user.ObjectId, user.DisplayName, now);
            LogAdded(id, cleanDeployment, user.ObjectId);
            return Changed(settings with { Models = [.. settings.Models, added] });
        }, cancellationToken);
    }

    /// <summary>Removes a model that isn't active for any job.</summary>
    public Task<AiModelsResult> DeleteModelAsync(string id, CancellationToken cancellationToken) =>
        UpdateAsync(settings =>
        {
            if (settings.Find(id) is not { } model)
            {
                return (new AiModelsResult.NotFound("No such model."), null);
            }

            if (settings.Active.FirstOrDefault(a => a.Value.ModelId == id) is { Value: not null } inUse)
            {
                return (new AiModelsResult.Conflict(
                    $"{model.Label} is the active model for {AiJobs.Words(inUse.Key)}. Choose another one first."), null);
            }

            // A job's "previous" that points at it goes back to the default.
            var activeNow = settings.Active.ToDictionary(a => a.Key, a => a.Value.PreviousModelId == id ? a.Value with { PreviousModelId = null } : a.Value);
            return Changed(new AiModelSettings([.. settings.Models.Where(m => m.Id != id)], activeNow));
        }, cancellationToken);

    /// <summary>Makes a model the job's active one, if it has passed that job's tests.</summary>
    public Task<AiModelsResult> SetActiveAsync(AiJob job, string modelId, CurrentUser user, CancellationToken cancellationToken) =>
        UpdateAsync(settings =>
        {
            if (settings.Find(modelId) is not { } model)
            {
                return (new AiModelsResult.NotFound("No such model."), null);
            }

            if (!model.Jobs.Contains(job))
            {
                return (new AiModelsResult.Conflict($"{model.Label} hasn't passed the tests for {AiJobs.Words(job)}."), null);
            }

            settings.Active.TryGetValue(job, out var before);
            if (before?.ModelId == modelId)
            {
                return (new AiModelsResult.Done(settings), null);
            }

            LogChanged(AiJobs.Id(job), model.Deployment, user.ObjectId);
            return Changed(WithActive(settings, job, new ActiveSelection(modelId, user.ObjectId, user.DisplayName, clock.GetUtcNow(), before?.ModelId)));
        }, cancellationToken);

    /// <summary>Goes back to the job's previous model, or to its default if it had none. Doing it again undoes it.</summary>
    public Task<AiModelsResult> RevertAsync(AiJob job, CurrentUser user, CancellationToken cancellationToken) =>
        UpdateAsync(settings =>
        {
            if (!settings.Active.TryGetValue(job, out var current))
            {
                return (new AiModelsResult.NotFound($"{AiJobs.Words(job)} is already on its default model."), null);
            }

            if (current.PreviousModelId is null)
            {
                LogChanged(AiJobs.Id(job), "default", user.ObjectId);
                return Changed(settings with { Active = settings.Active.Where(a => a.Key != job).ToDictionary(a => a.Key, a => a.Value) });
            }

            if (settings.Find(current.PreviousModelId) is not { } previous)
            {
                return (new AiModelsResult.Conflict("The previous model has been removed."), null);
            }

            LogChanged(AiJobs.Id(job), previous.Deployment, user.ObjectId);
            return Changed(WithActive(settings, job, new ActiveSelection(previous.Id, user.ObjectId, user.DisplayName, clock.GetUtcNow(), current.ModelId)));
        }, cancellationToken);

    private static AiModelSettings WithActive(AiModelSettings settings, AiJob job, ActiveSelection selection)
    {
        var next = settings.Active.ToDictionary(a => a.Key, a => a.Value);
        next[job] = selection;
        return settings with { Active = next };
    }

    private static (AiModelsResult, AiModelSettings?) Changed(AiModelSettings updated) => (new AiModelsResult.Done(updated), updated);

    /// <summary>Applies <paramref name="change"/> to the stored settings: it returns a result, and the new settings if
    /// something must be written. Retries on a write conflict.</summary>
    private async Task<AiModelsResult> UpdateAsync(
        Func<AiModelSettings, (AiModelsResult Result, AiModelSettings? ToWrite)> change, CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxRounds; round++)
        {
            var current = await repository.GetAsync(cancellationToken);
            var (result, toWrite) = change(current?.Value ?? AiModelSettings.Empty);
            if (toWrite is null)
            {
                return result;
            }

            var written = current is null
                ? await repository.TryCreateAsync(toWrite, cancellationToken)
                : await repository.TryReplaceAsync(current, toWrite, cancellationToken);
            if (written)
            {
                active.Invalidate();
                return result;
            }
        }

        throw new InvalidOperationException($"The AI model settings kept changing; gave up after {MaxRounds} tries.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AI model {ModelId} registered for deployment {Deployment} (user {UserId}).")]
    private partial void LogAdded(string modelId, string deployment, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "AI model for {Job} is now {Deployment} (user {UserId}).")]
    private partial void LogChanged(string job, string deployment, Guid userId);
}
