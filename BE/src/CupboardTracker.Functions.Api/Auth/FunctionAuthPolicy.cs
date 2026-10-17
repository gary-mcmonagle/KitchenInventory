using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Azure.Functions.Worker;

namespace CupboardTracker.Functions.Api.Auth;

/// <summary>Marks an HTTP function that anyone may call without signing in (e.g. the deploy smoke check's ping).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PublicEndpointAttribute : Attribute;

/// <summary>
/// Which functions need a signed-in user: every HTTP function at platform level Anonymous, unless it's marked
/// <see cref="PublicEndpointAttribute"/>. Function-key HTTP functions (checked by the platform), timers and the change
/// feed are left alone. So a new HTTP endpoint is protected unless someone deliberately marks it public.
/// </summary>
public static class FunctionAuthPolicy
{
    private static readonly ConcurrentDictionary<string, bool> Cache = new();

    /// <param name="entryPoint">The function's entry point, e.g. <c>CupboardTracker.Functions.Api.MeFunction.Run</c>.</param>
    public static bool RequiresUser(string entryPoint) =>
        Cache.GetOrAdd(entryPoint, static e => RequiresUser(Resolve(e)));

    public static bool RequiresUser(MethodInfo? method)
    {
        if (method is null)
        {
            return true; // unknown: fail closed
        }

        var trigger = method.GetParameters()
            .Select(p => p.GetCustomAttribute<HttpTriggerAttribute>())
            .FirstOrDefault(a => a is not null);
        return trigger is { AuthLevel: AuthorizationLevel.Anonymous }
            && method.GetCustomAttribute<PublicEndpointAttribute>() is null;
    }

    private static MethodInfo? Resolve(string entryPoint)
    {
        var dot = entryPoint.LastIndexOf('.');
        var type = dot > 0 ? typeof(FunctionAuthPolicy).Assembly.GetType(entryPoint[..dot]) : null;
        return type?.GetMethod(entryPoint[(dot + 1)..], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
    }
}
