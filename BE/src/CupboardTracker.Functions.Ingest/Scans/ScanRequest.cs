using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Scans;

namespace CupboardTracker.Functions.Ingest.Scans;

/// <summary>Turns a POST /api/scans body into a command (JSON shape only; the use case validates the values),
/// and a result into an HTTP status and body. See docs/api.md.</summary>
public static class ScanRequest
{
    public const int MaxBodyBytes = 16 * 1024;

    private static readonly string[] KnownFields = ["scanId", "barcode", "deviceId", "role", "scannedAt", "clientSha"];

    public static (RecordScanCommand? Command, string? Error) Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
        {
            return (null, $"Body is too large (at most {MaxBodyBytes} bytes).");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "Body must be a JSON object.");
            }

            var known = new Dictionary<string, string?>();
            var extra = new Dictionary<string, JsonElement>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (KnownFields.Contains(property.Name))
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return (null, $"{property.Name} must be a string.");
                    }

                    known[property.Name] = property.Value.GetString();
                }
                else
                {
                    extra[property.Name] = property.Value.Clone();
                }
            }

            return (new RecordScanCommand(
                known.GetValueOrDefault("scanId"),
                known.GetValueOrDefault("barcode"),
                known.GetValueOrDefault("deviceId"),
                known.GetValueOrDefault("role"),
                known.GetValueOrDefault("scannedAt"),
                known.GetValueOrDefault("clientSha"),
                extra), null);
        }
    }

    /// <summary>202 for a recorded scan, including a resend (the same 2xx either way); 400 with reasons otherwise.</summary>
    public static (HttpStatusCode Status, object Body) ToResponse(RecordScanResult result) => result switch
    {
        RecordScanResult.Recorded recorded => (HttpStatusCode.Accepted, new { scanId = recorded.ScanId }),
        RecordScanResult.Invalid invalid => (HttpStatusCode.BadRequest, new { errors = invalid.Errors }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    public static object Errors(string error) => new { errors = new[] { error } };
}
