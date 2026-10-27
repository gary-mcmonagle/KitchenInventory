using System.Globalization;
using System.Net;
using System.Text.Json;
using CupboardTracker.Application.AiModels;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Settings;

/// <summary>The Foundry models people register and choose in the app (#120). All need a signed-in user. See docs/api.md.</summary>
public sealed class AiModelFunctions(ManageAiModels manage, AiModelDefaults defaults)
{
    public const int MaxBodyBytes = 4 * 1024;

    /// <summary><c>GET /api/settings/ai-models</c>: the registered models, each job's active one, and the defaults.</summary>
    [Function("aiModels")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/ai-models")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var response = await JsonAsync(request, HttpStatusCode.OK, ToJson(await manage.GetAsync(cancellationToken), defaults));
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    /// <summary><c>PUT /api/settings/ai-models/models/{id}</c> with <c>{ "label": "…", "deployment": "…" }</c>: registers a
    /// model (deployment name as shown in Foundry), or renames one.</summary>
    [Function("putAiModel")]
    public async Task<HttpResponseData> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/ai-models/models/{id}")] HttpRequestData request,
        string id, FunctionContext context, CancellationToken cancellationToken)
    {
        var (label, deployment, error) = ParseModel(await ReadAsync(request, cancellationToken));
        if (error is not null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        return await RespondAsync(request, await manage.PutModelAsync(id, label, deployment, context.GetCurrentUser(), cancellationToken));
    }

    /// <summary><c>DELETE /api/settings/ai-models/models/{id}</c>: not allowed while it's a job's active model.</summary>
    [Function("deleteAiModel")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "settings/ai-models/models/{id}")] HttpRequestData request,
        string id, CancellationToken cancellationToken) =>
        await RespondAsync(request, await manage.DeleteModelAsync(id, cancellationToken));

    /// <summary><c>PUT /api/settings/ai-models/active/{job}</c> with <c>{ "modelId": "…" }</c>: <c>recipes</c>, <c>photos</c>
    /// or <c>genericNames</c>. Only a model that passed that job's tests.</summary>
    [Function("setActiveAiModel")]
    public async Task<HttpResponseData> SetActive(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/ai-models/active/{job}")] HttpRequestData request,
        string job, FunctionContext context, CancellationToken cancellationToken)
    {
        if (AiJobs.Parse(job) is not { } parsed)
        {
            return await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such job." });
        }

        var (modelId, error) = ParseActive(await ReadAsync(request, cancellationToken));
        if (error is not null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        return await RespondAsync(request, await manage.SetActiveAsync(parsed, modelId!, context.GetCurrentUser(), cancellationToken));
    }

    /// <summary><c>POST /api/settings/ai-models/active/{job}/revert</c>: back to the previous model (or the default).</summary>
    [Function("revertActiveAiModel")]
    public async Task<HttpResponseData> Revert(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settings/ai-models/active/{job}/revert")] HttpRequestData request,
        string job, FunctionContext context, CancellationToken cancellationToken) =>
        AiJobs.Parse(job) is { } parsed
            ? await RespondAsync(request, await manage.RevertAsync(parsed, context.GetCurrentUser(), cancellationToken))
            : await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such job." });

    internal static (string? Label, string? Deployment, string? Error) ParseModel(byte[] body)
    {
        const string Shape = "Body must be { \"label\": \"…\", \"deployment\": \"…\" }.";
        if (body.Length is 0 || body.Length > MaxBodyBytes)
        {
            return (null, null, Shape);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String
                && root.TryGetProperty("deployment", out var deployment) && deployment.ValueKind == JsonValueKind.String
                ? (label.GetString(), deployment.GetString(), null)
                : (null, null, Shape);
        }
        catch (JsonException)
        {
            return (null, null, "Body must be a JSON object.");
        }
    }

    internal static (string? ModelId, string? Error) ParseActive(byte[] body)
    {
        const string Shape = "Body must be { \"modelId\": \"…\" }.";
        if (body.Length is 0 || body.Length > MaxBodyBytes)
        {
            return (null, Shape);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modelId", out var id) && id.ValueKind == JsonValueKind.String
                ? (id.GetString(), null)
                : (null, Shape);
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(AiModelsResult result, AiModelDefaults modelDefaults) => result switch
    {
        AiModelsResult.Done d => (HttpStatusCode.OK, ToJson(d.Settings, modelDefaults)),
        AiModelsResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        AiModelsResult.NotFound n => (HttpStatusCode.NotFound, new { error = n.Error }),
        AiModelsResult.Conflict c => (HttpStatusCode.Conflict, new { error = c.Reason }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static object ToJson(AiModelSettings settings, AiModelDefaults modelDefaults) => new
    {
        models = settings.Models.Select(m => new
        {
            id = m.Id,
            label = m.Label,
            deployment = m.Deployment,
            temperatureMode = TemperatureModes.Id(m.TemperatureMode),
            jobs = m.Jobs.Select(AiJobs.Id),
            addedBy = m.AddedBy,
            addedByName = m.AddedByName,
            addedAt = Timestamp(m.AddedAt),
            tests = m.Tests,
        }),
        // Each job's active model, or null while it's on its default.
        active = AiJobs.All.ToDictionary(AiJobs.Id, job => settings.Active.TryGetValue(job, out var a)
            ? (object?)new
            {
                modelId = a.ModelId,
                changedBy = a.ChangedBy,
                changedByName = a.ChangedByName,
                changedAt = Timestamp(a.ChangedAt),
                previousModelId = a.PreviousModelId,
            }
            : null),
        // What each job uses until a model is made active (the app settings, else gpt-4o).
        defaults = AiJobs.All.ToDictionary(AiJobs.Id, modelDefaults.For),
    };

    private async Task<HttpResponseData> RespondAsync(HttpRequestData request, AiModelsResult result)
    {
        var (status, body) = ToResponse(result, defaults);
        return await JsonAsync(request, status, body);
    }

    private static async Task<byte[]> ReadAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData request, HttpStatusCode status, object body)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, WebJson));
        return response;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}
