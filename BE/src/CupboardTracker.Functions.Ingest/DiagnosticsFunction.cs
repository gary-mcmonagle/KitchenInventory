using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Functions.Ingest;

/// <summary>
/// <c>POST /api/diagnostics/test-error</c> (function key): logs one Error trace tagged diagnosticTest=true so the
/// "Cupboard Tracker errors" alert can be tested (#48). Touches no data.
/// </summary>
public sealed partial class DiagnosticsFunction(ILogger<DiagnosticsFunction> logger)
{
    [Function("diagnosticsTestError")]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "diagnostics/test-error")] HttpRequestData request)
    {
        LogTestError(diagnosticTest: true);
        var response = request.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        response.WriteString("Logged one test Error (diagnosticTest=true). Nothing else was changed.");
        return response;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Diagnostic test error from POST /api/diagnostics/test-error (diagnosticTest={diagnosticTest}); not a real problem.")]
    private partial void LogTestError(bool diagnosticTest);
}
