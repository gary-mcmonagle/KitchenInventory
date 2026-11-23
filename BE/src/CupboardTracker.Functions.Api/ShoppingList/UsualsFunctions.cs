using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Usuals;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Infrastructure.Usuals;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Functions.Api.ShoppingList;

/// <summary>"Add usuals" (#134): start a job and poll it. Both need a signed-in user. See docs/api.md.</summary>
public sealed class UsualsFunctions(UsualsJobs usuals)
{
    /// <summary><c>POST /api/shopping-list/usuals</c>: starts working out what's due. <b>202</b> with the job.</summary>
    [Function("startUsuals")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "shopping-list/usuals")] HttpRequestData request,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var (status, body) = ToResponse(await usuals.StartAsync(context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>GET /api/shopping-list/usuals/{jobId}</c>: where the job is, and the selection once it's ready.</summary>
    [Function("getUsuals")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "shopping-list/usuals/{jobId:guid}")] HttpRequestData request,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var job = await usuals.GetAsync(jobId, cancellationToken);
        var (status, body) = job is null
            ? (HttpStatusCode.NotFound, (object)new { error = "No such usuals job (it may have expired)." })
            : (HttpStatusCode.OK, Wire(job));
        return await JsonAsync(request, status, body, noStore: true);
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(UsualsStartResult result) => result switch
    {
        UsualsStartResult.Started s => (HttpStatusCode.Accepted, Wire(s.Job)),
        UsualsStartResult.RateLimited => (HttpStatusCode.TooManyRequests, new { error = "That's a lot of usuals for one hour. Try again a bit later." }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    internal static object Wire(UsualsJob job) => new
    {
        jobId = job.JobId,
        status = job.Status.ToString().ToLowerInvariant(),
        step = job.Step,
        method = job.Method,
        items = job.Items.Select(i => new
        {
            name = i.Name,
            genericNameId = i.GenericNameId,
            aisle = i.Aisle,
            band = i.Band.ToString().ToLowerInvariant(),
            why = i.Why,
            productId = i.ProductId,
        }),
    };

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData request, HttpStatusCode status, object body, bool noStore = false)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        if (noStore)
        {
            response.Headers.Add("Cache-Control", "no-store");
        }

        await response.WriteStringAsync(JsonSerializer.Serialize(body, WebJson));
        return response;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}

/// <summary>Works out a usuals job (#134). A bad message is logged and dropped; a message delivered twice finds the job done.</summary>
public sealed partial class UsualsJobFunction(UsualsJobs usuals, ILogger<UsualsJobFunction> logger)
{
    [Function("usualsJob")]
    public async Task Run(
        [QueueTrigger(StorageQueueUsualsScheduler.QueueName, Connection = "AzureWebJobsStorage")] string message, CancellationToken cancellationToken)
    {
        Guid? jobId;
        try
        {
            jobId = StorageQueueUsualsScheduler.Deserialize(message);
        }
        catch (JsonException)
        {
            jobId = null;
        }

        if (jobId is { } id)
        {
            await usuals.RunAsync(id, cancellationToken);
        }
        else
        {
            LogUnreadable();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A usuals message couldn't be read; dropped.")]
    private partial void LogUnreadable();
}
