using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>The Cosmos containers from docs/data-model.md.</summary>
public sealed class CosmosContainers(CosmosClient client, string databaseName)
{
    public Container ScanEvents { get; } = client.GetContainer(databaseName, "scanEvents");

    public Container Devices { get; } = client.GetContainer(databaseName, "devices");
}
