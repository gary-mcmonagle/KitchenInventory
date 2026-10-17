using CupboardTracker.Application.Ping;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Ingest;

/// <summary><c>GET /api/ping</c>: reports that the app is up and which commit is deployed. Function-key auth.</summary>
public sealed class PingFunction(GetPingStatus getPingStatus)
{
    [Function("ping")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "ping")] HttpRequestData request)
    {
        var status = getPingStatus.Execute();
        var response = request.CreateResponse();
        await response.WriteAsJsonAsync(new { status = status.Status, commit = status.Commit });
        return response;
    }
}
