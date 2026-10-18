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

    /// <summary>Opened, frozen and defrosted (#65): five questions in one request, worded as checked against Jev
    /// before building (docs/spikes/65-jev-unit-state.py). The usual storage is part of the state.</summary>
    public async Task<StateEstimatesResult> EstimateStatesAsync(
        ProductDescription product, Storage usualStorage, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return new StateEstimatesResult.NotConfigured();
        }

        var state = State(product);
        state["storage"] = StorageName(usualStorage);
        return await client.AskAsync(state, StateQuestions(), cancellationToken) switch
        {
            JevResponse.Answered { Answers: var answers } => ReadStates(answers),
            JevResponse.Unavailable { Reason: var reason } => new StateEstimatesResult.Unavailable(reason),
            JevResponse.Refused { Reason: var reason } => new StateEstimatesResult.Failed(reason),
            var other => throw new InvalidOperationException($"Unexpected Jev response {other}."),
        };
    }

    internal const string IconInstructions =
        "Pick the icon that best shows what this food or drink product is, so someone can recognise it at a glance "
        + "in a list of kitchen stock. Choose the closest kind of food, not the brand.";

    /// <summary>Aisle and icon (#71, #72): a choice over the aisles and one over the curated icons, in one request,
    /// asking only what's wanted (spike #60's wording).</summary>
    public async Task<ClassificationResult> ClassifyAsync(
        ProductDescription product, Storage? usualStorage, bool aisle, bool icon, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return new ClassificationResult.NotConfigured();
        }

        var state = State(product);
        if (usualStorage is { } storage)
        {
            state["storage"] = StorageName(storage);
        }

        return await client.AskAsync(state, ClassificationQuestions(aisle, icon), cancellationToken) switch
        {
            JevResponse.Answered { Answers: var answers } => ReadClassification(answers),
            JevResponse.Unavailable { Reason: var reason } => new ClassificationResult.Unavailable(reason),
            JevResponse.Refused { Reason: var reason } => new ClassificationResult.Failed(reason),
            var other => throw new InvalidOperationException($"Unexpected Jev response {other}."),
        };
    }

    internal static JsonObject ClassificationQuestions(bool aisle, bool icon)
    {
        var questions = new JsonObject();
        if (aisle)
        {
            var criteria = new JsonObject();
            foreach (var a in Aisles.All)
            {
                criteria[a.Id] = a.Description;
            }

            questions["aisle"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = "Which supermarket aisle this product would be found in.",
                ["criteria"] = criteria,
            };
        }

        if (icon)
        {
            var criteria = new JsonObject();
            foreach (var name in ProductIcons.Names)
            {
                criteria[name] = name.Replace('-', ' ');
            }

            questions["icon"] = new JsonObject { ["type"] = "choice", ["instructions"] = IconInstructions, ["criteria"] = criteria };
        }

        return questions;
    }

    internal static ClassificationResult ReadClassification(JsonObject answers)
    {
        static (string? Choice, double Confidence) Choice(JsonNode? answer) =>
            answer is JsonObject o && o["choice"] is JsonValue c && c.TryGetValue<string>(out var choice)
                ? (choice, o["confidence"] is JsonValue v && v.TryGetValue<double>(out var conf) ? conf : 0)
                : (null, 0);

        var (aisle, aisleConfidence) = Choice(answers["aisle"]);
        var (icon, iconConfidence) = Choice(answers["icon"]);
        if (answers["aisle"] is not null && Aisles.Find(aisle) is null)
        {
            return new ClassificationResult.Failed($"unknown aisle answer {aisle}");
        }

        return new ClassificationResult.Classified(new Classification(aisle, aisleConfidence, icon, iconConfidence));
    }

    internal static JsonObject StateQuestions() => new()
    {
        ["opened_storage"] = Choice("Where this product should be kept at home once it has been opened.", withFreezer: true),
        ["opened_shelf_life"] = Duration(
            "How long this product stays good to eat once it has been opened, kept where it should be once opened."),
        ["frozen_shelf_life"] = Duration(
            "How long this product keeps in a home freezer if it is frozen on the day it was bought, before the quality suffers."),
        ["defrosted_storage"] = Choice(
            "Where this product should be kept once it has been taken out of the freezer and defrosted.", withFreezer: false),
        ["defrosted_shelf_life"] = Duration(
            "How long this product stays good to eat once it has been frozen and then defrosted, kept where it should be once defrosted."),
    };

    internal static StateEstimatesResult ReadStates(JsonObject answers)
    {
        StateEstimate? Read(string duration, string? storage, Storage fixedStorage)
        {
            if (Score(answers[duration]) is not var (days, confidence, low))
            {
                return null;
            }

            var where = storage is null ? fixedStorage : ParseStorage(answers[storage]?["choice"]?.GetValue<string>(), fixedStorage);
            return new StateEstimate(days, where, confidence, low);
        }

        var opened = Read("opened_shelf_life", "opened_storage", Storage.Fridge);
        var frozen = Read("frozen_shelf_life", null, Storage.Freezer);
        var defrosted = Read("defrosted_shelf_life", "defrosted_storage", Storage.Fridge);
        return opened is null || frozen is null || defrosted is null
            ? new StateEstimatesResult.Failed("missing a state answer")
            : new StateEstimatesResult.Estimated(new StateEstimates(opened, frozen, defrosted));
    }

    private static JsonObject Duration(string instructions) => new()
    {
        ["type"] = "score",
        ["instructions"] = instructions,
        ["criteria"] = new JsonArray([.. Levels.Select(l => (JsonNode)JsonValue.Create(l.Words)!)]),
    };

    private static JsonObject Choice(string instructions, bool withFreezer)
    {
        var criteria = new JsonObject { ["cupboard"] = "Cupboard or worktop, at room temperature", ["fridge"] = "Fridge" };
        if (withFreezer)
        {
            criteria["freezer"] = "Freezer";
        }

        return new JsonObject { ["type"] = "choice", ["instructions"] = instructions, ["criteria"] = criteria };
    }

    /// <summary>A score answer's days (median, or 25th percentile below 0.5 confidence), or null if unreadable.</summary>
    private static (int Days, double Confidence, bool Low)? Score(JsonNode? answer)
    {
        if (answer is not JsonObject score
            || score["probabilities"] is not JsonObject probabilities
            || score["confidence"] is not JsonValue confidenceValue
            || !confidenceValue.TryGetValue<double>(out var confidence))
        {
            return null;
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
            return null;
        }

        var low = confidence < LowConfidenceBelow;
        return (Levels[Percentile(distribution, low ? 0.25 : 0.5)].Days, confidence, low);
    }

    private static string StorageName(Storage storage) => storage switch
    {
        Storage.Fridge => "fridge",
        Storage.Freezer => "freezer",
        _ => "cupboard",
    };

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

    private static Storage ParseStorage(string? choice, Storage otherwise = Storage.Cupboard) => choice switch
    {
        "cupboard" => Storage.Cupboard,
        "fridge" => Storage.Fridge,
        "freezer" => Storage.Freezer,
        _ => otherwise,
    };
}
