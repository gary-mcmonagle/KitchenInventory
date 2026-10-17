using CupboardTracker.Application.Ping;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api;

/// <summary><c>GET /api/ping</c>: reports that the app is up and which commit is deployed. Public: the deploy smoke check
/// calls it without signing in.</summary>
public sealed class PingFunction(GetPingStatus getPingStatus)
{
    [Function("ping")]
    [PublicEndpoint]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")] HttpRequestData request)
    {
        var status = getPingStatus.Execute();
        var response = request.CreateResponse();
        await response.WriteAsJsonAsync(new { status = status.Status, commit = status.Commit });
        return response;
    }
}
