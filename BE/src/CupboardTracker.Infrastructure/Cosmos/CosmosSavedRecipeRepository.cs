using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.SavedRecipes;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// The savedRecipes container (#105): the household's saved recipes and Ready now ideas, one partition
/// (<c>householdId: "household"</c>), id = the app's id. A recipe stores <c>ingredients</c>, <c>servings</c> and
/// <c>steps</c>; a Ready now idea stores <c>uses</c>, <c>how</c> and <c>appliance</c>.
/// </summary>
public sealed class CosmosSavedRecipeRepository(CosmosContainers containers) : ISavedRecipeRepository
{
    public const string HouseholdId = "household";

    private static readonly PartitionKey Partition = new(HouseholdId);

    private Container Container => containers.SavedRecipes;

    public async Task<Versioned<SavedRecipe>?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await Container.ReadItemStreamAsync(id.ToString("D"), Partition, cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } d && CosmosJson.Text(d["type"]) == "savedRecipe"
            ? new Versioned<SavedRecipe>(FromDocument(d), CosmosJson.Text(d["_etag"])!)
            : null; // the container also holds meal plans (#119)
    }

    public async Task<IReadOnlyList<SavedRecipe>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await CosmosJson.QueryAsync(Container, new QueryDefinition("SELECT * FROM c WHERE c.type = 'savedRecipe'"), Partition, null, cancellationToken)).Select(FromDocument)];

    public async Task<bool> TryCreateAsync(SavedRecipe recipe, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(recipe));
        using var response = await Container.CreateItemStreamAsync(stream, Partition, cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> TryReplaceAsync(Versioned<SavedRecipe> current, SavedRecipe updated, CancellationToken cancellationToken)
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

    internal static JsonObject ToDocument(SavedRecipe r)
    {
        var readyNow = r.Kind == SavedRecipeKinds.ReadyNow;
        var items = new JsonArray([.. r.Ingredients.Select(i => (JsonNode)new JsonObject
        {
            ["name"] = i.Name,
            ["amount"] = i.Amount,
            ["productId"] = i.ProductId?.ToString("D"),
            ["genericName"] = i.GenericName,
            ["for"] = Diets.Id(i.For),
        })]);
        var d = new JsonObject
        {
            ["id"] = r.Id.ToString("D"),
            ["householdId"] = HouseholdId,
            ["type"] = "savedRecipe",
            ["kind"] = r.Kind,
            ["title"] = r.Title,
            ["originalTitle"] = r.OriginalTitle,
            ["meal"] = r.Meal.ToString().ToLowerInvariant(),
            ["totalMinutes"] = r.TotalMinutes,
            [readyNow ? "uses" : "ingredients"] = items,
            ["alsoNeeded"] = new JsonArray([.. r.AlsoNeeded.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["for"] = Diets.Id(a.For) })]),
            ["diet"] = Diets.Id(r.Diet),
            ["note"] = r.Note,
            ["savedBy"] = r.SavedBy.ToString("D"),
            ["savedByName"] = r.SavedByName,
            ["savedAt"] = CosmosJson.Timestamp(r.SavedAt),
            ["updatedBy"] = r.UpdatedBy.ToString("D"),
            ["updatedAt"] = CosmosJson.Timestamp(r.UpdatedAt),
        };
        if (readyNow)
        {
            d["how"] = r.How;
            d["appliance"] = r.Appliance;
        }
        else
        {
            d["servings"] = r.Servings;
            d["servingsMeat"] = r.ServingsMeat;
            d["servingsVeg"] = r.ServingsVeg;
            d["steps"] = new JsonArray([.. r.Steps.Select(s => (JsonNode)new JsonObject { ["text"] = s.Text, ["for"] = Diets.Id(s.For) })]);
        }

        return d;
    }

    internal static SavedRecipe FromDocument(JsonObject d)
    {
        var kind = CosmosJson.Text(d["kind"]) ?? SavedRecipeKinds.Recipe;
        // Saved before #110, steps and extra items were plain strings: they read as for everyone.
        static IEnumerable<(string Text, PlateFor For)> Items(JsonNode? n, string field) =>
            (n as JsonArray ?? []).Select(x => x is JsonObject o
                ? (CosmosJson.Text(o[field]) ?? "", Diets.ParsePlate(CosmosJson.Text(o["for"])))
                : (CosmosJson.Text(x) ?? "", PlateFor.All)).Where(x => x.Item1.Length > 0);
        static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        return new SavedRecipe(
            Guid.Parse(CosmosJson.Text(d["id"])!),
            kind,
            CosmosJson.Text(d["title"]) ?? "",
            CosmosJson.Text(d["originalTitle"]) ?? CosmosJson.Text(d["title"]) ?? "",
            Enum.TryParse<RecipeMeal>(CosmosJson.Text(d["meal"]), true, out var meal) ? meal : RecipeMeal.Dinner,
            Int(d["totalMinutes"]) ?? 0,
            Int(d["servings"]),
            [.. ((d[kind == SavedRecipeKinds.ReadyNow ? "uses" : "ingredients"] as JsonArray) ?? []).OfType<JsonObject>().Select(i => new SavedIngredient(
                CosmosJson.Text(i["name"]) ?? "",
                CosmosJson.Text(i["amount"]),
                Guid.TryParse(CosmosJson.Text(i["productId"]), out var p) ? p : null,
                CosmosJson.Text(i["genericName"]),
                Diets.ParsePlate(CosmosJson.Text(i["for"]))))],
            [.. Items(d["steps"], "text").Select(x => new RecipeStep(x.Text, x.For))],
            [.. Items(d["alsoNeeded"], "name").Select(x => new ExtraItem(x.Text, x.For))],
            CosmosJson.Text(d["how"]),
            CosmosJson.Text(d["appliance"]),
            CosmosJson.Text(d["note"]) ?? "",
            Guid.TryParse(CosmosJson.Text(d["savedBy"]), out var by) ? by : Guid.Empty,
            CosmosJson.Text(d["savedByName"]) ?? "",
            CosmosJson.ParseTimestamp(d["savedAt"]) ?? DateTimeOffset.MinValue,
            Guid.TryParse(CosmosJson.Text(d["updatedBy"]), out var ub) ? ub : Guid.Empty,
            CosmosJson.ParseTimestamp(d["updatedAt"]) ?? DateTimeOffset.MinValue,
            Diets.Parse(CosmosJson.Text(d["diet"])) ?? Diet.Any,
            Int(d["servingsMeat"]) ?? 0,
            Int(d["servingsVeg"]) ?? 0);
    }
}
