using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CupboardTracker.Infrastructure.Jev;

/// <summary>The outcome of one Jev request (after retries).</summary>
public abstract record JevResponse
{
    /// <summary>200: the <c>answers</c> object, keyed by question id.</summary>
    public sealed record Answered(JsonObject Answers) : JevResponse;

    /// <summary>429/529, 5xx, timeouts and network errors, still failing after the last attempt.</summary>
    public sealed record Unavailable(string Reason) : JevResponse;

    /// <summary>400/401/413/415/422 or an unreadable body: a bug or bad configuration, never retried.</summary>
    public sealed record Refused(string Reason) : JevResponse;
}

/// <summary>
/// Jev (TypeSafe System One): <c>POST /v1/systemone</c> with typed questions (docs/spikes/40-jev.md). No .NET SDK, so
/// this is a typed <see cref="HttpClient"/>. Retries 429, 529, 5xx, timeouts and network errors with back-off
/// (honouring <c>retry-after</c>); never retries other 4xx. The key goes only in the Authorization header and is
/// never logged or put in a reason.
/// </summary>
public sealed class JevClient(HttpClient http, JevOptions options, TimeProvider clock)
{
    public async Task<JevResponse> AskAsync(JsonNode state, JsonObject questions, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["state"] = state.DeepClone(), ["model"] = options.Model, ["questions"] = questions.DeepClone() }
            .ToJsonString();
        var delay = options.FirstRetryDelay;
        var reason = "no attempts";
        for (var attempt = 1; attempt <= options.Attempts; attempt++)
        {
            var (response, retryAfter) = await TryOnceAsync(body, cancellationToken);
            if (response is not JevResponse.Unavailable unavailable)
            {
                return response;
            }

            reason = unavailable.Reason;
            if (attempt < options.Attempts)
            {
                var wait = retryAfter is { } after && after > delay ? after : delay;
                await Task.Delay(wait < options.MaxRetryDelay ? wait : options.MaxRetryDelay, clock, cancellationToken);
                delay *= 2;
            }
        }

        return new JevResponse.Unavailable($"{reason} (after {options.Attempts} attempts)");
    }

    private async Task<(JevResponse Response, TimeSpan? RetryAfter)> TryOnceAsync(string body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUrl, "/v1/systemone"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            using var response = await http.SendAsync(request, timeout.Token);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests || status == 529 || status >= 500)
            {
                return (new JevResponse.Unavailable($"HTTP {status}"), RetryAfter(response));
            }

            if (!response.IsSuccessStatusCode)
            {
                return (new JevResponse.Refused($"HTTP {status}"), null);
            }

            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            try
            {
                return JsonNode.Parse(text)?["answers"] is JsonObject answers
                    ? (new JevResponse.Answered(answers), null)
                    : (new JevResponse.Refused("no answers in the response"), null);
            }
            catch (JsonException)
            {
                return (new JevResponse.Refused("non-JSON response"), null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (new JevResponse.Unavailable("timeout"), null);
        }
        catch (HttpRequestException e)
        {
            return (new JevResponse.Unavailable($"network error ({e.HttpRequestError})"), null);
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - clock.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return response.Headers.TryGetValues("retry-after", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}
