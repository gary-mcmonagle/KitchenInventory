using System.Globalization;
using System.Text.Json.Nodes;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Infrastructure.Jev;

/// <summary>
/// Shelf life from Jev (#52, docs/spikes/40-jev.md): a <c>score</c> over ten worded durations and a <c>choice</c> of
/// usual storage, in one request per product. The level used is the <b>median</b> of the score's probabilities, or
/// the <b>25th percentile</b> (a cautious estimate) when confidence is below 0.5.
/// </summary>
public sealed class JevShelfLifeEstimator(JevClient client, JevOptions options) : IShelfLifeEstimator
{
    public const double LowConfidenceBelow = 0.5;

    /// <summary>The worded levels Jev scores, and the days each one means (spike #40).</summary>
    internal static readonly (string Words, int Days)[] Levels =
    [
        ("1-2 days", 2), ("3-4 days", 4), ("About a week", 7), ("About 2 weeks", 14), ("About a month", 30),
        ("About 3 months", 90), ("About 6 months", 180), ("About a year", 365), ("About 2 years", 730),
        ("3 years or more", 1095),
    ];

    public async Task<ShelfLifeResult> EstimateAsync(ProductDescription product, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return new ShelfLifeResult.NotConfigured();
        }

        return await client.AskAsync(State(product), Questions(), cancellationToken) switch
        {
            JevResponse.Answered { Answers: var answers } => Read(answers),
            JevResponse.Unavailable { Reason: var reason } => new ShelfLifeResult.Unavailable(reason),
            JevResponse.Refused { Reason: var reason } => new ShelfLifeResult.Failed(reason),
            var other => throw new InvalidOperationException($"Unexpected Jev response {other}."),
        };
    }

    internal static JsonObject State(ProductDescription product)
    {
        var state = new JsonObject { ["name"] = product.Name };
        if (product.Brand is { } brand)
        {
            state["brand"] = brand;
        }

        if (product.Size is { } size)
        {
            state["size"] = size;
        }

        if (product.CategoryTags.Count > 0)
        {
            state["categories"] = new JsonArray([.. product.CategoryTags.Select(t => (JsonNode)JsonValue.Create(t)!)]);
        }

        return state;
    }

    internal static JsonObject Questions() => new()
    {
        ["shelf_life"] = new JsonObject
        {
            ["type"] = "score",
            ["instructions"] = "How long this product usually stays good to eat after it is bought, unopened and stored "
                + "the usual way for this kind of product.",
            ["criteria"] = new JsonArray([.. Levels.Select(l => (JsonNode)JsonValue.Create(l.Words)!)]),
        },
        ["storage"] = new JsonObject
        {
            ["type"] = "choice",
            ["instructions"] = "Where this product is normally stored at home before it is opened.",
            ["criteria"] = new JsonObject
            {
                ["cupboard"] = "Cupboard or worktop, at room temperature",
                ["fridge"] = "Fridge",
                ["freezer"] = "Freezer",
            },
        },
    };

    internal static ShelfLifeResult Read(JsonObject answers)
    {
        if (answers["shelf_life"] is not JsonObject shelfLife
            || shelfLife["probabilities"] is not JsonObject probabilities
            || shelfLife["confidence"] is not JsonValue confidenceValue
            || !confidenceValue.TryGetValue<double>(out var confidence))
        {
            return new ShelfLifeResult.Failed("no shelf_life answer");
        }

        var distribution = new double[Levels.Length];
        foreach (var (key, value) in probabilities)
        {
            if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var level)
                && level >= 0 && level < Levels.Length && value is JsonValue v && v.TryGetValue<double>(out var p))
            {
                distribution[level] = p;
            }
        }

        if (distribution.Sum() <= 0)
        {
            return new ShelfLifeResult.Failed("empty shelf_life probabilities");
        }

        var low = confidence < LowConfidenceBelow;
        var chosen = Percentile(distribution, low ? 0.25 : 0.5);
        var storage = ParseStorage(answers["storage"]?["choice"]?.GetValue<string>());
        return new ShelfLifeResult.Estimated(new ShelfLifeEstimate(Levels[chosen].Days, storage, confidence, low));
    }

    /// <summary>The lowest level where the running total of probabilities reaches <paramref name="fraction"/>.</summary>
    internal static int Percentile(IReadOnlyList<double> distribution, double fraction)
    {
        var total = distribution.Sum();
        var running = 0.0;
        for (var level = 0; level < distribution.Count; level++)
        {
            running += distribution[level] / total;
            if (running >= fraction - 1e-9)
            {
                return level;
            }
        }

        return distribution.Count - 1;
    }

    private static Storage ParseStorage(string? choice) => choice switch
    {
        "fridge" => Storage.Fridge,
        "freezer" => Storage.Freezer,
        _ => Storage.Cupboard,
    };
}
