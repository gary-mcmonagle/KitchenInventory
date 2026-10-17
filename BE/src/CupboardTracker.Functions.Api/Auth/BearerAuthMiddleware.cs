using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Users;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Functions.Api.Auth;

/// <summary>
/// Checks the bearer token for functions that need a user (<see cref="FunctionAuthPolicy"/>), answering 401 or 403
/// itself; on success the function can read the caller with <see cref="CurrentUserExtensions.GetCurrentUser"/>.
/// </summary>
public sealed partial class BearerAuthMiddleware(ITokenValidator validator, ILogger<BearerAuthMiddleware> logger)
    : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        if (!FunctionAuthPolicy.RequiresUser(context.FunctionDefinition.EntryPoint))
        {
            await next(context);
            return;
        }

        var request = await context.GetHttpRequestDataAsync()
            ?? throw new InvalidOperationException($"{context.FunctionDefinition.Name} needs a user but isn't an HTTP function.");
        var header = request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null;

        switch (await validator.ValidateAsync(header, context.CancellationToken))
        {
            case AuthOutcome.Valid valid:
                context.Items[CurrentUserExtensions.ItemKey] = valid.User;
                await next(context);
                return;
            case AuthOutcome.Forbidden forbidden:
                LogRefused(context.FunctionDefinition.Name, 403, forbidden.Reason);
                context.GetInvocationResult().Value = await RefuseAsync(request, HttpStatusCode.Forbidden, forbidden.Reason, "insufficient_scope");
                return;
            case AuthOutcome.Unauthorized unauthorized:
                LogRefused(context.FunctionDefinition.Name, 401, unauthorized.Reason);
                context.GetInvocationResult().Value = await RefuseAsync(request, HttpStatusCode.Unauthorized, unauthorized.Reason, "invalid_token");
                return;
        }
    }

    private static async Task<HttpResponseData> RefuseAsync(HttpRequestData request, HttpStatusCode status, string reason, string error)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("WWW-Authenticate", $"Bearer error=\"{error}\"");
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { error = reason }));
        return response;
    }

    // Information, not Warning: refused calls are expected (expired sessions, bots) and must not page anyone (#48).
    [LoggerMessage(Level = LogLevel.Information, Message = "Refused {Function} with {Status}: {Reason}")]
    private partial void LogRefused(string function, int status, string reason);
}

public static class CurrentUserExtensions
{
    internal const string ItemKey = "CupboardTracker.CurrentUser";

    /// <summary>The signed-in caller. Only valid in functions that need a user (the middleware has checked them).</summary>
    public static CurrentUser GetCurrentUser(this FunctionContext context) =>
        context.Items.TryGetValue(ItemKey, out var user) && user is CurrentUser current
            ? current
            : throw new InvalidOperationException("No signed-in user: is this function marked [PublicEndpoint]?");
}
