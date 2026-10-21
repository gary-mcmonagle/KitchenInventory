using System.Globalization;
using System.Text.Json.Nodes;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Infrastructure.Jev;

namespace CupboardTracker.Infrastructure.Recipes;

/// <summary>
/// Suggested additions for the Adjust sheet (#104): one Jev <c>choice</c> question, "which of these would improve this
/// recipe most as an addition?", over in-stock names (<c>a0</c>, <c>a1</c>, …) plus "Nothing would improve it". Jev
/// answers with a probability for every option, so one question ranks them all (spike #40). The state is the recipe's
/// title, meal, ingredient names and method in short: no personal data.
/// </summary>
public sealed class JevRecipeAdditionRanker(JevClient client, JevOptions jevOptions) : IRecipeAdditionRanker
{
    internal const string Instructions =
        "Which one of these, added to this recipe, would improve it most? Think like a home cook: it should suit the dish "
        + "and the meal, and go with the ingredients already in it. If none of them would make it better, choose 'Nothing "
        + "would improve it'.";

    internal const string NothingKey = "none";

    public async Task<AdditionRanking> RankAsync(RecipeSketch recipe, IReadOnlyList<string> options, CancellationToken cancellationToken)
    {
        if (!jevOptions.IsConfigured)
        {
            return new AdditionRanking.Unavailable("not configured");
        }

        if (options.Count == 0)
        {
            return new AdditionRanking.Ranked([], 1);
        }

        return await client.AskAsync(State(recipe), Question(options), cancellationToken) switch
        {
            JevResponse.Answered { Answers: var answers } => Read(answers, options.Count),
            JevResponse.Unavailable { Reason: var reason } => new AdditionRanking.Unavailable(reason),
            JevResponse.Refused { Reason: var reason } => new AdditionRanking.Unavailable(reason),
            var other => throw new InvalidOperationException($"Unexpected Jev response {other}."),
        };
    }

    internal static JsonObject State(RecipeSketch recipe) => new()
    {
        ["recipe"] = recipe.Title,
        ["meal"] = recipe.Meal.ToString().ToLowerInvariant(),
        ["ingredients"] = new JsonArray([.. recipe.Ingredients.Select(i => (JsonNode)JsonValue.Create(i)!)]),
        ["method"] = recipe.Method,
    };

    internal static JsonObject Question(IReadOnlyList<string> options)
    {
        var criteria = new JsonObject();
        for (var i = 0; i < options.Count; i++)
        {
            criteria[$"a{i}"] = options[i];
        }

        criteria[NothingKey] = "Nothing would improve it";
        return new JsonObject { ["addition"] = new JsonObject { ["type"] = "choice", ["instructions"] = Instructions, ["criteria"] = criteria } };
    }

    /// <summary>The probabilities in the order asked; an option Jev left out counts as 0.</summary>
    internal static AdditionRanking Read(JsonObject answers, int count)
    {
        if (answers["addition"]?["probabilities"] is not JsonObject probabilities)
        {
            return new AdditionRanking.Unavailable("no probabilities in Jev's answer");
        }

        var ranked = new double[count];
        var nothing = 0.0;
        foreach (var (key, value) in probabilities)
        {
            if (value is not JsonValue v || !v.TryGetValue<double>(out var p))
            {
                continue;
            }

            if (key == NothingKey)
            {
                nothing = p;
            }
            else if (key.StartsWith('a') && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < count)
            {
                ranked[i] = p;
            }
        }

        return new AdditionRanking.Ranked(ranked, nothing);
    }
}
