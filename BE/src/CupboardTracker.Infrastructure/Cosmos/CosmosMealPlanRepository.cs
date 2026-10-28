using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.Plans;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// Meal plans (#119) in the savedRecipes container, next to the saved recipes: one document per plan, <c>type: "mealPlan"</c>,
/// id = the plan's id, partition <c>householdId: "household"</c>, ETag-checked. The document is the plan as the API returns it.
/// No ttl: plans are kept until deleted.
/// </summary>
public sealed class CosmosMealPlanRepository(CosmosContainers containers) : IMealPlanRepository
{
    public const string Type = "mealPlan";

    private static readonly PartitionKey Partition = new(CosmosSavedRecipeRepository.HouseholdId);

    private Container Container => containers.SavedRecipes;

    public async Task<Versioned<MealPlan>?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await Container.ReadItemStreamAsync(id.ToString("D"), Partition, cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } d && CosmosJson.Text(d["type"]) == Type && MealPlanJson.FromNode(d) is { } plan
            ? new Versioned<MealPlan>(plan, CosmosJson.Text(d["_etag"])!)
            : null;
    }

    public async Task<bool> TryCreateAsync(MealPlan plan, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(plan));
        using var response = await Container.CreateItemStreamAsync(stream, Partition, cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> TryReplaceAsync(Versioned<MealPlan> current, MealPlan updated, CancellationToken cancellationToken)
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

    internal static JsonObject ToDocument(MealPlan plan)
    {
        var document = MealPlanJson.ToNode(plan);
        document["householdId"] = CosmosSavedRecipeRepository.HouseholdId;
        document["type"] = Type;
        return document;
    }
}

/// <summary>
/// What the household bought lately, for "usually buys" (#119, spike #124): units checked in or out since the start date, each
/// keyed by its product's generic name (else its display name), and the shopping list's memory (every name ever added, with a
/// count). Three small reads (about 17 RU in all on today's data); products are read in one projection.
/// </summary>
public sealed class CosmosPurchaseHistory(CosmosContainers containers) : IPurchaseHistory
{
    public async Task<(IReadOnlyList<PurchaseUnit> Units, IReadOnlyList<ListMemoryEntry> Memory)> GetAsync(
        DateTimeOffset since, CancellationToken cancellationToken)
    {
        var start = CosmosJson.Timestamp(since);
        var unitRows = await CosmosJson.QueryAsync(
            containers.InventoryItems,
            new QueryDefinition("SELECT c.productId, c.checkedInAt, c.checkedOutAt FROM c WHERE c.checkedInAt >= @start OR c.checkedOutAt >= @start")
                .WithParameter("@start", start), partitionKey: null, max: null, cancellationToken, "PlanHistoryUnits");
        var productRows = unitRows.Count == 0
            ? []
            : await CosmosJson.QueryAsync(
                containers.Products,
                new QueryDefinition("SELECT c.id, c.displayName, c.genericName FROM c WHERE c.type = 'product'"),
                partitionKey: null, max: null, cancellationToken, "PlanHistoryProducts");
        var names = productRows.ToDictionary(
            p => CosmosJson.Text(p["id"]) ?? "", p => CosmosJson.Text(p["genericName"]) ?? CosmosJson.Text(p["displayName"]) ?? "");
        var units = new List<PurchaseUnit>();
        foreach (var row in unitRows)
        {
            if (names.TryGetValue(CosmosJson.Text(row["productId"]) ?? "", out var key) && key.Length > 0
                && CosmosJson.ParseTimestamp(row["checkedInAt"]) is { } checkedIn)
            {
                units.Add(new PurchaseUnit(key, checkedIn, CosmosJson.ParseTimestamp(row["checkedOutAt"])));
            }
        }

        var memoryRows = await CosmosJson.QueryAsync(
            containers.ShoppingList,
            new QueryDefinition("SELECT c.name, c.timesAdded, c.aisle FROM c WHERE c.type = 'memory'"),
            new PartitionKey("household"), max: null, cancellationToken, "PlanHistoryList");
        var memory = memoryRows.Select(m => new ListMemoryEntry(
            CosmosJson.Text(m["name"]) ?? "", m["timesAdded"] is JsonValue t && t.TryGetValue<int>(out var n) ? n : 1, CosmosJson.Text(m["aisle"])))
            .Where(m => m.Name.Length > 0).ToList();
        return (units, memory);
    }
}
