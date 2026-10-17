using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Hubs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Ingest.Heartbeats;

/// <summary><c>POST /api/heartbeat</c>: the hub reporting in. Function-key auth (401 from the platform without it).</summary>
public sealed class HeartbeatFunction(RecordHeartbeat recordHeartbeat)
{
    [Function("heartbeat")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "heartbeat")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (command, error) = HeartbeatRequest.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (command is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, HeartbeatRequest.Errors(error!));
        }

        var (status, body) = HeartbeatRequest.ToResponse(await recordHeartbeat.ExecuteAsync(command, cancellationToken));
        return await JsonAsync(request, status, body);
    }

    // WriteAsJsonAsync resets the status to 200, so set it and serialise explicitly.
    private static async Task<HttpResponseData> JsonAsync(HttpRequestData request, HttpStatusCode status, object body)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, WebJson));
        return response;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}
