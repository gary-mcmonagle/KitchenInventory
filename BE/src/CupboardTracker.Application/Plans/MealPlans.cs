using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CupboardTracker.Application.Plans;

/// <summary>The meal types a plan can hold (#119), as the API and the model use them.</summary>
public static class PlanMealTypes
{
    public const string Breakfast = "breakfast";
    public const string Lunch = "lunch";
    public const string Dinner = "dinner";

    /// <summary>In the order the plan shows them.</summary>
    public static readonly IReadOnlyList<string> All = [Breakfast, Lunch, Dinner];

    public static bool IsValid(string? type) => type is Breakfast or Lunch or Dinner;
}

public static class PlanStatuses
{
    public const string Generating = "generating";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

/// <summary>What the household chose (#119): how many days, which meal types (one meal per day per type), meat or veg, kit,
/// whether to build around what needs using up, and a wish. <see cref="Equipment"/> is null for all kit.</summary>
public sealed record PlanChoices(
    int Days, IReadOnlyList<string> MealTypes, string Diet, IReadOnlyList<string>? Equipment, bool PreferUseFirst, string? Comments)
{
    public int Count(string type) => MealTypes.Contains(type) ? Days : 0;

    public int TotalMeals => MealTypes.Count * Days;
}

/// <summary>An in-stock product a planned meal uses, with its name as it was when the plan was made.</summary>
public sealed record PlannedProduct(Guid ProductId, string Name);

/// <summary>
/// One meal of a plan (#119): an outline, not a recipe: title, a line, servings, the stock it uses and the things to buy
/// (short generic names). <see cref="Recipe"/> is the full recipe once it has been written (#126), kept as given so a kept
/// meal keeps it. <see cref="Warnings"/> are the "Check before cooking" lines.
/// </summary>
public sealed record PlannedMeal(
    string Id,
    string Type,
    string Title,
    string Summary,
    int Servings,
    IReadOnlyList<PlannedProduct> Uses,
    IReadOnlyList<PlannedProduct> UsesUp,
    IReadOnlyList<string> ToBuy,
    bool Keep,
    IReadOnlyList<string> Warnings,
    JsonObject? Recipe = null);

/// <summary>A household meal plan: a pool of meals, not tied to days. <see cref="AddedToListAt"/> is for #125.</summary>
public sealed record MealPlan(
    Guid Id,
    Guid CreatedBy,
    string CreatedByName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Status,
    string? Error,
    PlanChoices Choices,
    IReadOnlyList<PlannedMeal> Meals,
    DateTimeOffset? AddedToListAt = null);

/// <summary>"Shares peppers, onions with 2 other meals": things to buy that this meal needs and others do too.</summary>
public sealed record MealShares(IReadOnlyList<string> Names, int OtherMeals);

/// <summary>A meal with the facts its card shows, worked out from the whole plan (never from the model's words).</summary>
public sealed record PlannedMealView(PlannedMeal Meal, int UsesCount, MealShares? Shares, IReadOnlyList<string> UseFirst);

public sealed record MealPlanView(MealPlan Plan, IReadOnlyList<PlannedMealView> Meals);

/// <summary>How plans are stored and returned: the same shape in Cosmos and in the API.</summary>
public static class MealPlanJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonObject ToNode(MealPlan plan) => (JsonObject)JsonSerializer.SerializeToNode(plan, Options)!;

    public static MealPlan? FromNode(JsonNode? node) => node is null ? null : node.Deserialize<MealPlan>(Options);

    public static JsonObject ToNode(PlannedMeal meal) => (JsonObject)JsonSerializer.SerializeToNode(meal, Options)!;

    public static PlannedMeal? MealFromNode(JsonNode? node) => node is null ? null : node.Deserialize<PlannedMeal>(Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        options.Converters.Add(new UtcTimestampConverter());
        return options;
    }

    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }
}
