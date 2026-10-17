using CupboardTracker.Domain;

namespace CupboardTracker.Application.Hubs;

/// <summary>Hubs, with optimistic concurrency: a heartbeat and the offline check can race.</summary>
public interface IHubRepository
{
    Task<Versioned<Hub>?> GetAsync(string hubId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Versioned<Hub>>> ListAsync(CancellationToken cancellationToken);

    /// <summary>False if a hub with that ID already exists (someone else created it first).</summary>
    Task<bool> TryCreateAsync(Hub hub, CancellationToken cancellationToken);

    /// <summary>False if the hub changed since <paramref name="current"/> was read.</summary>
    Task<bool> TryReplaceAsync(Versioned<Hub> current, Hub updated, CancellationToken cancellationToken);
}
