using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Scans;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Ingest.Scans;

/// <summary><c>POST /api/scans</c>: records a scan event from the hub. Function-key auth (401 from the platform without it).</summary>
public sealed class ScansFunction(RecordScan recordScan)
{
    [Function("scans")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "scans")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (command, error) = ScanRequest.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (command is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, ScanRequest.Errors(error!));
        }

        var result = await recordScan.ExecuteAsync(command, cancellationToken);
        var (status, body) = ScanRequest.ToResponse(result);
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
