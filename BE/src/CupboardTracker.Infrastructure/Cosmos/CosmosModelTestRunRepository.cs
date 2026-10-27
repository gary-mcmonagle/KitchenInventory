using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.AiModels;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// Model test runs (#121) in the short-lived <c>pushSeen</c> container (partition <c>/eventKey</c>, per-document ttl):
/// <c>testrun:&lt;runId&gt;</c> holds the run as the API returns it, and <c>testrun-active:&lt;modelId&gt;</c> marks a model's
/// run in progress (so a second start returns it). Both expire on their own.
/// </summary>
public sealed class CosmosModelTestRunRepository(CosmosContainers containers) : IModelTestRunRepository
{
    public static readonly TimeSpan RunTtl = TimeSpan.FromDays(1);
    public static readonly TimeSpan MarkTtl = TimeSpan.FromMinutes(20);

    public async Task CreateAsync(ModelTestRun run, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(run));
        using var response = await containers.PushSeen.CreateItemStreamAsync(stream, new PartitionKey(Key(run.RunId)), cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<Versioned<ModelTestRun>?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        using var response = await containers.PushSeen.ReadItemStreamAsync(Key(runId), new PartitionKey(Key(runId)), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } document && ModelTestJson.FromNode(document) is { } run
            ? new Versioned<ModelTestRun>(run, CosmosJson.Text(document["_etag"])!)
            : null;
    }

    public async Task<bool> TryReplaceAsync(Versioned<ModelTestRun> current, ModelTestRun updated, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(updated));
        using var response = await containers.PushSeen.ReplaceItemStreamAsync(
            stream, Key(updated.RunId), new PartitionKey(Key(updated.RunId)), new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task SaveAsync(ModelTestRun run, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(run));
        using var response = await containers.PushSeen.UpsertItemStreamAsync(stream, new PartitionKey(Key(run.RunId)), cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<Guid?> GetActiveRunIdAsync(string modelId, CancellationToken cancellationToken)
    {
        using var response = await containers.PushSeen.ReadItemStreamAsync(ActiveKey(modelId), new PartitionKey(ActiveKey(modelId)), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } document && Guid.TryParse(CosmosJson.Text(document["runId"]), out var id) ? id : null;
    }

    public async Task<bool> TryMarkActiveAsync(string modelId, Guid runId, CancellationToken cancellationToken)
    {
        var document = new JsonObject
        {
            ["id"] = ActiveKey(modelId),
            ["eventKey"] = ActiveKey(modelId),
            ["type"] = "modelTestActive",
            ["runId"] = runId.ToString("D"),
            ["ttl"] = (int)MarkTtl.TotalSeconds,
        };
        using var stream = CosmosJson.ToStream(document);
        using var response = await containers.PushSeen.CreateItemStreamAsync(stream, new PartitionKey(ActiveKey(modelId)), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task ClearActiveAsync(string modelId, CancellationToken cancellationToken)
    {
        using var response = await containers.PushSeen.DeleteItemStreamAsync(ActiveKey(modelId), new PartitionKey(ActiveKey(modelId)), cancellationToken: cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    internal static string Key(Guid runId) => $"testrun:{runId:D}";

    internal static string ActiveKey(string modelId) => $"testrun-active:{modelId}";

    internal static JsonObject ToDocument(ModelTestRun run)
    {
        var document = ModelTestJson.ToNode(run);
        document["id"] = Key(run.RunId);
        document["eventKey"] = Key(run.RunId);
        document["type"] = "modelTestRun";
        document["ttl"] = (int)RunTtl.TotalSeconds;
        return document;
    }
}
