using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.Ratings;
using CupboardTracker.Application.Recipes;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// The ratings container (#128, spike #127): the household's ratings, one partition (<c>householdId: "household"</c>),
/// id = the app's id. A rating stores the person, the 👍 or 👎, its reasons, when and in what context, and a small snapshot
/// of the dish. The container also has a vector policy for an <c>embedding</c> written by a later card; reads here never ask
/// for it (a 256-float vector is most of a document), and a replace drops it, which that card queues again on every change.
/// </summary>
public sealed class CosmosRatingRepository(CosmosContainers containers) : IRatingRepository
{
    public const string HouseholdId = "household";

    private static readonly PartitionKey Partition = new(HouseholdId);

    /// <summary>Every field but the embedding.</summary>
    private const string Fields =
        "c.id, c.personId, c.personName, c.rating, c.reasons, c.dislikedIngredients, c.context, c.ratedAt, c.updatedAt, "
        + "c.kind, c.title, c.normalizedTitle, c.meal, c.diet, c.totalMinutes, c.ingredients, c.equipment, c.savedRecipeId, c.planId, c.mealId, c._etag";

    private Container Container => containers.Ratings;

    public async Task<Versioned<Rating>?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await Container.ReadItemStreamAsync(id.ToString("D"), Partition, cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } d
            ? new Versioned<Rating>(FromDocument(d), CosmosJson.Text(d["_etag"])!)
            : null;
    }

    public async Task<IReadOnlyList<Rating>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await CosmosJson.QueryAsync(Container, new QueryDefinition($"SELECT {Fields} FROM c"), Partition, null, cancellationToken)).Select(FromDocument)];

    public async Task<IReadOnlyList<Rating>> FindOwnAsync(Guid personId, string kind, string normalizedTitle, CancellationToken cancellationToken) =>
        [.. (await CosmosJson.QueryAsync(
            Container,
            new QueryDefinition($"SELECT {Fields} FROM c WHERE c.personId = @person AND c.kind = @kind AND c.normalizedTitle = @title")
                .WithParameter("@person", personId.ToString("D")).WithParameter("@kind", kind).WithParameter("@title", normalizedTitle),
            Partition, null, cancellationToken)).Select(FromDocument)];

    public async Task UpsertAsync(Rating rating, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(rating));
        using var response = await Container.UpsertItemStreamAsync(stream, Partition, cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> TryReplaceAsync(Versioned<Rating> current, Rating updated, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(updated));
        using var response = await Container.ReplaceItemStreamAsync(
            stream, updated.Id.ToString("D"), Partition, new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await Container.DeleteItemStreamAsync(id.ToString("D"), Partition, cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    internal static JsonObject ToDocument(Rating r)
    {
        var s = r.Snapshot;
        var d = new JsonObject
        {
            ["id"] = r.Id.ToString("D"),
            ["householdId"] = HouseholdId,
            ["personId"] = r.PersonId.ToString("D"),
            ["personName"] = r.PersonName,
            ["rating"] = r.Value,
            ["reasons"] = new JsonArray([.. r.Reasons.Select(x => (JsonNode)JsonValue.Create(x)!)]),
            ["dislikedIngredients"] = new JsonArray([.. r.DislikedIngredients.Select(x => (JsonNode)JsonValue.Create(x)!)]),
            ["context"] = r.Context,
            ["ratedAt"] = CosmosJson.Timestamp(r.RatedAt),
            ["updatedAt"] = CosmosJson.Timestamp(r.UpdatedAt),
            ["kind"] = s.Kind,
            ["title"] = s.Title,
            ["normalizedTitle"] = r.NormalizedTitle,
            ["meal"] = s.Meal.ToString().ToLowerInvariant(),
            ["diet"] = Diets.Id(s.Diet),
            ["totalMinutes"] = s.TotalMinutes,
            ["ingredients"] = new JsonArray([.. s.Ingredients.Select(i => (JsonNode)new JsonObject { ["name"] = i.Name, ["genericName"] = i.GenericName })]),
            ["equipment"] = new JsonArray([.. s.Equipment.Select(x => (JsonNode)JsonValue.Create(x)!)]),
        };
        if (s.SavedRecipeId is { } saved)
        {
            d["savedRecipeId"] = saved.ToString("D");
        }

        if (s.PlanId is { } plan)
        {
            d["planId"] = plan.ToString("D");
        }

        if (s.MealId is { } meal)
        {
            d["mealId"] = meal;
        }

        return d;
    }

    internal static Rating FromDocument(JsonObject d)
    {
        static List<string> Strings(JsonNode? n) => [.. (n as JsonArray ?? []).Select(CosmosJson.Text).OfType<string>()];
        var snapshot = new RatingSnapshot(
            CosmosJson.Text(d["kind"]) ?? RatingKinds.Recipe,
            CosmosJson.Text(d["title"]) ?? "",
            Enum.TryParse<RecipeMeal>(CosmosJson.Text(d["meal"]), true, out var meal) ? meal : RecipeMeal.Dinner,
            Diets.Parse(CosmosJson.Text(d["diet"])) ?? Diet.Any,
            d["totalMinutes"] is JsonValue m && m.TryGetValue<int>(out var minutes) ? minutes : 0,
            [.. (d["ingredients"] as JsonArray ?? []).OfType<JsonObject>().Select(i => new RatingIngredient(CosmosJson.Text(i["name"]) ?? "", CosmosJson.Text(i["genericName"])))],
            Strings(d["equipment"]),
            Guid.TryParse(CosmosJson.Text(d["savedRecipeId"]), out var saved) ? saved : null,
            Guid.TryParse(CosmosJson.Text(d["planId"]), out var plan) ? plan : null,
            CosmosJson.Text(d["mealId"]));
        return new Rating(
            Guid.Parse(CosmosJson.Text(d["id"])!),
            Guid.TryParse(CosmosJson.Text(d["personId"]), out var person) ? person : Guid.Empty,
            CosmosJson.Text(d["personName"]) ?? "",
            CosmosJson.Text(d["rating"]) ?? RatingValues.Up,
            Strings(d["reasons"]),
            Strings(d["dislikedIngredients"]),
            CosmosJson.Text(d["context"]) ?? RatingContexts.Suggested,
            CosmosJson.ParseTimestamp(d["ratedAt"]) ?? DateTimeOffset.MinValue,
            CosmosJson.ParseTimestamp(d["updatedAt"]) ?? DateTimeOffset.MinValue,
            snapshot);
    }
}
