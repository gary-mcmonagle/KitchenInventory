using System.Text.Json.Nodes;

namespace CupboardTracker.Infrastructure;

/// <summary>Reads why Foundry refused a request (#120), so the log says more than "HTTP 400".</summary>
internal static class FoundryErrors
{
    public const int MaxMessageLength = 300;

    /// <summary>
    /// The error <c>code</c> and <c>message</c> from a Foundry error body, e.g. <c>unsupported_value</c> and "Unsupported
    /// value: 'temperature' does not support 0.7 with this model…". Never the request: the message is cut to
    /// <see cref="MaxMessageLength"/> characters on one line, and anything that isn't an error object gives "unknown".
    /// </summary>
    public static (string Code, string Message) Reason(string? responseBody)
    {
        try
        {
            if (JsonNode.Parse(responseBody ?? "") is JsonObject { } root && root["error"] is JsonObject error)
            {
                var code = Clean(error["code"]?.ToString(), 60);
                var message = Clean(error["message"]?.ToString(), MaxMessageLength);
                return (code.Length > 0 ? code : "unknown", message.Length > 0 ? message : "no message");
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON (a gateway page, say): nothing useful to log.
        }

        return ("unknown", "no message");
    }

    private static string Clean(string? text, int max)
    {
        var line = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > max ? line[..max] + "…" : line;
    }
}
