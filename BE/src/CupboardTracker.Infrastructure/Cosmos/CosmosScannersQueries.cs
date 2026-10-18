using CupboardTracker.Application.Scanners;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Stock;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>The scanners screen's reads (#55): small cross-partition queries.</summary>
public sealed class CosmosScannersQueries(CosmosContainers containers) : IScannersQueries
{
    public async Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var rows = await CosmosJson.QueryAsync(containers.Devices,
            new QueryDefinition("SELECT * FROM c WHERE NOT IS_DEFINED(c.kind) AND IS_DEFINED(c.role)"),
            partitionKey: null, max: null, cancellationToken);
        var devices = new List<Device>();
        foreach (var d in rows)
        {
            if (RoleNames.TryParse(CosmosJson.Text(d["role"]), out var role))
            {
                devices.Add(new Device(CosmosJson.Text(d["id"])!, role,
                    CosmosJson.ParseTimestamp(d["lastSeenAt"]) ?? DateTimeOffset.MinValue, CosmosJson.ParseTimestamp(d["lastScanAt"])));
            }
        }

        return devices;
    }

    public async Task<IReadOnlyList<UnmatchedCheckOut>> GetUndismissedUnmatchedAsync(CancellationToken cancellationToken)
    {
        var rows = await CosmosJson.QueryAsync(containers.UnmatchedCheckOuts,
            new QueryDefinition("SELECT * FROM c WHERE NOT IS_DEFINED(c.dismissed) OR c.dismissed = false"),
            partitionKey: null, max: null, cancellationToken);
        return rows.Select(CosmosUnmatchedCheckOutRepository.ToUnmatched).ToList();
    }

    public async Task<IReadOnlyList<Unit>> GetCheckedOutSinceAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.status = 'checkedOut' AND c.checkedOutAt >= @since")
            .WithParameter("@since", CosmosJson.Timestamp(since));
        var rows = await CosmosJson.QueryAsync(containers.InventoryItems, query, partitionKey: null, max: null, cancellationToken);
        return rows.Select(CosmosUnitRepository.ToUnit).ToList();
    }
}
