using CupboardTracker.Application.Ping;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api;

/// <summary><c>GET /api/ping</c>: reports that the app is up and which commit is deployed. No auth yet; Entra bearer-token auth comes with the Entra cards (spike #35).</summary>
public sealed class PingFunction(GetPingStatus getPingStatus)
{
    [Function("ping")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")] HttpRequestData request)
    {
        var status = getPingStatus.Execute();
        var response = request.CreateResponse();
        await response.WriteAsJsonAsync(new { status = status.Status, commit = status.Commit });
        return response;
    }
}
