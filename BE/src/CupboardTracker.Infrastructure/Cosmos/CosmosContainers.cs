using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>The Cosmos containers from docs/data-model.md.</summary>
public sealed class CosmosContainers(CosmosClient client, string databaseName)
{
    public Container ScanEvents { get; } = client.GetContainer(databaseName, "scanEvents");

    public Container Devices { get; } = client.GetContainer(databaseName, "devices");

    public Container Products { get; } = client.GetContainer(databaseName, "products");

    public Container InventoryItems { get; } = client.GetContainer(databaseName, "inventoryItems");

    public Container UnmatchedCheckOuts { get; } = client.GetContainer(databaseName, "unmatchedCheckOuts");

    public Container SentDigests { get; } = client.GetContainer(databaseName, "sentDigests");

    public Container PushSubscriptions { get; } = client.GetContainer(databaseName, "pushSubscriptions");

    public Container PushSeen { get; } = client.GetContainer(databaseName, "pushSeen");

    public Container ShoppingList { get; } = client.GetContainer(databaseName, "shoppingList");

    public Container SavedRecipes { get; } = client.GetContainer(databaseName, "savedRecipes");
}
