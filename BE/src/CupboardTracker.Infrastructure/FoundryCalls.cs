using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application.AiModels;

namespace CupboardTracker.Infrastructure;

/// <summary>Reports each Foundry call to the <see cref="AiCallLog"/> of the flow running it (#121): numbers and Foundry's
/// own error code and message only, never the prompt, image or reply.</summary>
internal static class FoundryCalls
{
    /// <summary>A reply arrived (any status). <paramref name="started"/> is a <see cref="Stopwatch.GetTimestamp"/>.</summary>
    public static void Reply(string deployment, long started, HttpStatusCode status, bool success, string text)
    {
        var ms = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (success)
        {
            var (prompt, completion, model, filtered) = Usage(text);
            AiCallLog.Record(new AiCall(deployment, ms, prompt, completion, (int)status, null, null, model, filtered ? AiCall.Filtered : AiCall.Ok));
            return;
        }

        var (code, message) = FoundryErrors.Reason(text);
        var outcome = status == HttpStatusCode.BadRequest && text.Contains("content_filter", StringComparison.Ordinal) ? AiCall.Filtered
            : status != HttpStatusCode.TooManyRequests && (int)status < 500 ? AiCall.Refused
            : AiCall.Failed;
        AiCallLog.Record(new AiCall(deployment, ms, 0, 0, (int)status, code, message, null, outcome));
    }

    /// <summary>No usable reply: <paramref name="outcome"/> is one of the <see cref="AiCall"/> outcomes.</summary>
    public static void Failure(string deployment, long started, string outcome) =>
        AiCallLog.Record(new AiCall(deployment, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, 0, null, null, null, null, outcome));

    internal static (int Prompt, int Completion, string? Model, bool Filtered) Usage(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject root)
            {
                return (
                    root["usage"]?["prompt_tokens"] is JsonValue p && p.TryGetValue<int>(out var pn) ? pn : 0,
                    root["usage"]?["completion_tokens"] is JsonValue c && c.TryGetValue<int>(out var cn) ? cn : 0,
                    root["model"] is JsonValue m && m.TryGetValue<string>(out var mn) ? mn : null,
                    (string?)root["choices"]?[0]?["finish_reason"] == "content_filter");
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unreadable: the caller treats it the same way.
        }

        return (0, 0, null, false);
    }
}
