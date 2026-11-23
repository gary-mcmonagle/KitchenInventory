using System.Diagnostics;
using System.Text.Json.Nodes;
using CupboardTracker.Application.Usuals;
using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Infrastructure.Jev;

/// <summary>
/// "Is this due?" for the usuals (#134, spike #135): one yes/no (<c>noul</c>) question per candidate, each in its own request with that
/// candidate's own weekly history as the state, a few at a time. One request for everyone was measured 0.02 to 0.04 AUC worse. The state
/// holds only the name, the aisle and one sentence of history: no stock, no shelf life, no people. If any request is left unanswered the
/// whole judgement is unavailable and the plain rule is used instead.
/// </summary>
public sealed partial class JevUsualsJudge(JevClient client, JevOptions jevOptions, UsualsSettings settings, ILogger<JevUsualsJudge> logger) : IUsualsJudge
{
    internal const string Instruction =
        "They are about to write the list for the next weekly shop. Given how often this item has been in their weekly shops, "
        + "it will probably be needed on this one.";

    internal const string QuestionId = "due";

    public async Task<UsualsJudgement> JudgeAsync(IReadOnlyList<UsualsCandidate> candidates, CancellationToken cancellationToken)
    {
        if (!jevOptions.IsConfigured)
        {
            return new UsualsJudgement.Unavailable("not configured");
        }

        var probabilities = new double[candidates.Count];
        var unanswered = 0;
        var tokens = 0;
        var started = Stopwatch.StartNew();
        var question = Question();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, settings.JevParallelism), CancellationToken = cancellationToken },
            async (i, token) =>
            {
                var response = await client.AskAsync(State(candidates[i]), question, token);
                if (response is JevResponse.Answered { Answers: var answers, InputTokens: var used } && Read(answers) is { } p)
                {
                    probabilities[i] = p;
                    Interlocked.Add(ref tokens, used);
                }
                else
                {
                    Interlocked.Increment(ref unanswered);
                }
            });

        LogAsked(candidates.Count, candidates.Count - unanswered, tokens, started.ElapsedMilliseconds);
        return unanswered > 0
            ? new UsualsJudgement.Unavailable($"{unanswered} of {candidates.Count} not answered")
            : new UsualsJudgement.Judged(probabilities, tokens);
    }

    internal static JsonObject Question() =>
        new() { [QuestionId] = new JsonObject { ["type"] = "noul", ["instructions"] = Instruction } };

    /// <summary>The item, its aisle and one sentence of weekly history, the layout that tested best (spike #135, <c>w2</c>).</summary>
    internal static JsonObject State(UsualsCandidate c)
    {
        var n = c.Weeks.Count;
        var yesNo = string.Join(", ", c.Weeks.Select(w => w ? "yes" : "no"));
        var since = c.WeeksSinceLast == 0 ? "bought in the most recent shop" : $"last bought {c.WeeksSinceLast} shops ago";
        return new JsonObject
        {
            ["item"] = c.Name,
            ["aisle"] = AisleWords(c.Aisle),
            ["history"] = $"The household does one big shop a week. In the last {n} weekly shops (oldest first), was this bought that "
                + $"week? {yesNo}. Bought in {c.WeeksBought} of {n} weeks; {since}.",
        };
    }

    private static string AisleWords(string id) =>
        Aisles.All.FirstOrDefault(a => a.Id == id)?.Name.Replace("&", "and").ToLowerInvariant() ?? "other";

    internal static double? Read(JsonObject answers) =>
        answers[QuestionId]?["noul"] is JsonValue v && v.TryGetValue<double>(out var p) && p is >= 0 and <= 1 ? p : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Usuals Jev: {Asked} questions, {Answered} answered, {Tokens} input tokens, {Ms} ms.")]
    private partial void LogAsked(int asked, int answered, int tokens, long ms);
}
