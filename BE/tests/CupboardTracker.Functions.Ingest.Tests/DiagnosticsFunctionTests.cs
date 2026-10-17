using System.Reflection;
using CupboardTracker.Functions.Ingest;
using Microsoft.Azure.Functions.Worker;

namespace CupboardTracker.Functions.Ingest.Tests;

public sealed class DiagnosticsFunctionTests
{
    [Fact]
    public void The_test_error_endpoint_needs_a_function_key_and_only_accepts_post()
    {
        var parameter = typeof(DiagnosticsFunction).GetMethod(nameof(DiagnosticsFunction.Run))!.GetParameters()[0];
        var trigger = parameter.GetCustomAttribute<HttpTriggerAttribute>()!;

        Assert.Equal(AuthorizationLevel.Function, trigger.AuthLevel);
        Assert.Equal(["post"], trigger.Methods!);
        Assert.Equal("diagnostics/test-error", trigger.Route);
    }
}
