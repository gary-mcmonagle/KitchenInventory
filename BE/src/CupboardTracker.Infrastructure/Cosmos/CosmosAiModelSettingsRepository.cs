using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.AiModels;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// The registered Foundry models and each job's active one (#120): one document in the <c>settings</c> container,
/// id = partition key = <c>aiModels</c>. Created once (a second create conflicts), then replaced under its ETag.
/// </summary>
public sealed class CosmosAiModelSettingsRepository(CosmosContainers containers) : IAiModelSettingsRepository
{
    public const string Id = "aiModels";

    private static readonly PartitionKey Partition = new(Id);

    public async Task<Versioned<AiModelSettings>?> GetAsync(CancellationToken cancellationToken)
    {
        using var response = await containers.Settings.ReadItemStreamAsync(Id, Partition, cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } document
            ? new Versioned<AiModelSettings>(FromDocument(document), CosmosJson.Text(document["_etag"])!)
            : null;
    }

    public async Task<bool> TryCreateAsync(AiModelSettings settings, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(settings));
        using var response = await containers.Settings.CreateItemStreamAsync(stream, Partition, cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> TryReplaceAsync(Versioned<AiModelSettings> current, AiModelSettings updated, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(updated));
        using var response = await containers.Settings.ReplaceItemStreamAsync(
            stream, Id, Partition, new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    internal static JsonObject ToDocument(AiModelSettings settings)
    {
        var active = new JsonObject();
        foreach (var (job, selection) in settings.Active)
        {
            active[AiJobs.Id(job)] = new JsonObject
            {
                ["modelId"] = selection.ModelId,
                ["changedBy"] = selection.ChangedBy.ToString("D"),
                ["changedByName"] = selection.ChangedByName,
                ["changedAt"] = CosmosJson.Timestamp(selection.ChangedAt),
                ["previousModelId"] = selection.PreviousModelId,
            };
        }

        return new JsonObject
        {
            ["id"] = Id,
            ["type"] = "aiModels",
            ["models"] = new JsonArray([.. settings.Models.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id,
                ["label"] = m.Label,
                ["deployment"] = m.Deployment,
                ["temperatureMode"] = TemperatureModes.Id(m.TemperatureMode),
                ["jobs"] = new JsonArray([.. m.Jobs.Select(j => (JsonNode)JsonValue.Create(AiJobs.Id(j))!)]),
                ["addedBy"] = m.AddedBy.ToString("D"),
                ["addedByName"] = m.AddedByName,
                ["addedAt"] = CosmosJson.Timestamp(m.AddedAt),
                ["tests"] = m.Tests?.DeepClone(),
            })]),
            ["active"] = active,
        };
    }

    internal static AiModelSettings FromDocument(JsonObject document)
    {
        var models = (document["models"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new AiModel(
            CosmosJson.Text(m["id"]) ?? "",
            CosmosJson.Text(m["label"]) ?? "",
            CosmosJson.Text(m["deployment"]) ?? "",
            TemperatureModes.Parse(CosmosJson.Text(m["temperatureMode"])) ?? TemperatureMode.Usual,
            [.. (m["jobs"] as JsonArray ?? []).Select(j => AiJobs.Parse(CosmosJson.Text(j))).OfType<AiJob>()],
            Guid.TryParse(CosmosJson.Text(m["addedBy"]), out var by) ? by : Guid.Empty,
            CosmosJson.Text(m["addedByName"]) ?? "",
            CosmosJson.ParseTimestamp(m["addedAt"]) ?? DateTimeOffset.MinValue,
            m["tests"] as JsonObject)).ToList();

        var active = new Dictionary<AiJob, ActiveSelection>();
        foreach (var job in AiJobs.All)
        {
            if (document["active"]?[AiJobs.Id(job)] is JsonObject a && CosmosJson.Text(a["modelId"]) is { } modelId)
            {
                active[job] = new ActiveSelection(
                    modelId,
                    Guid.TryParse(CosmosJson.Text(a["changedBy"]), out var by) ? by : Guid.Empty,
                    CosmosJson.Text(a["changedByName"]) ?? "",
                    CosmosJson.ParseTimestamp(a["changedAt"]) ?? DateTimeOffset.MinValue,
                    CosmosJson.Text(a["previousModelId"]));
            }
        }

        return new AiModelSettings(models, active);
    }
}
