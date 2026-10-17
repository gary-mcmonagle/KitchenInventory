using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Hubs;

namespace CupboardTracker.Functions.Ingest.Heartbeats;

/// <summary>Turns a POST /api/heartbeat body into a command (JSON types only; the use case validates the values), and a
/// result into an HTTP status and body. See docs/api.md.</summary>
public static class HeartbeatRequest
{
    public const int MaxBodyBytes = 16 * 1024;

    public static (RecordHeartbeatCommand? Command, string? Error) Parse(ReadOnlySpan<byte> body)
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
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "Body must be a JSON object.");
            }

            string? hubId = null, sentAt = null, clientSha = null;
            long? uptime = null;
            List<string>? usb = null;
            List<ScannerPresenceCommand>? scanners = null;
            foreach (var p in root.EnumerateObject())
            {
                var v = p.Value;
                switch (p.Name)
                {
                    case "hubId" or "sentAt" or "clientSha" when v.ValueKind != JsonValueKind.String:
                        return (null, $"{p.Name} must be a string.");
                    case "hubId":
                        hubId = v.GetString();
                        break;
                    case "sentAt":
                        sentAt = v.GetString();
                        break;
                    case "clientSha":
                        clientSha = v.GetString();
                        break;
                    case "uptimeSeconds":
                        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var seconds))
                        {
                            return (null, "uptimeSeconds must be a whole number.");
                        }

                        uptime = seconds;
                        break;
                    case "usbInputDevices":
                        if (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String))
                        {
                            return (null, "usbInputDevices must be a list of strings.");
                        }

                        usb = v.EnumerateArray().Select(i => i.GetString()!).ToList();
                        break;
                    case "scanners":
                        if (v.ValueKind == JsonValueKind.Null)
                        {
                            break;
                        }

                        if (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.Object))
                        {
                            return (null, "scanners must be a list of { deviceId, present } objects.");
                        }

                        scanners = v.EnumerateArray().Select(i => new ScannerPresenceCommand(
                            i.TryGetProperty("deviceId", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                            i.TryGetProperty("present", out var pr) && pr.ValueKind is JsonValueKind.True or JsonValueKind.False
                                ? pr.GetBoolean()
                                : null)).ToList();
                        break;
                    default:
                        break; // unknown fields are ignored: the hub may report more later
                }
            }

            return (new RecordHeartbeatCommand(hubId, sentAt, clientSha, uptime, usb, scanners), null);
        }
    }

    /// <summary>202 when recorded; 400 with every reason otherwise.</summary>
    public static (HttpStatusCode Status, object Body) ToResponse(RecordHeartbeatResult result) => result switch
    {
        RecordHeartbeatResult.Recorded recorded => (HttpStatusCode.Accepted, new { hubId = recorded.HubId }),
        RecordHeartbeatResult.Invalid invalid => (HttpStatusCode.BadRequest, new { errors = invalid.Errors }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    public static object Errors(string error) => new { errors = new[] { error } };
}
