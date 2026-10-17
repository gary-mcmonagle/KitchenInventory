using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Auth;

/// <summary><c>GET /api/me</c>: who the bearer token says you are (#36). For checking sign-in end to end.</summary>
public static class MeFunction
{
    [Function("me")]
    public static async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "me")] HttpRequestData request,
        FunctionContext context)
    {
        var user = context.GetCurrentUser();
        var response = request.CreateResponse();
        await response.WriteAsJsonAsync(new { name = user.DisplayName, objectId = user.ObjectId });
        return response;
    }
}
