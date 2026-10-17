using CupboardTracker.Application.Hubs;
using CupboardTracker.Application.Notifications;
using CupboardTracker.Domain;

namespace CupboardTracker.Application.Tests.Hubs;

/// <summary>In-memory hubs with ETags, like Cosmos: a write with a stale version fails.</summary>
internal sealed class FakeHubs : IHubRepository
{
    private int version;

    public Dictionary<string, (Hub Hub, string ETag)> Stored { get; } = [];

    /// <summary>Runs just before a write is applied: lets a test make another writer win the race.</summary>
    public Action? BeforeWrite { get; set; }

    public void Put(Hub hub) => Stored[hub.HubId] = (hub, NextETag());

    public Task<Versioned<Hub>?> GetAsync(string hubId, CancellationToken cancellationToken) =>
        Task.FromResult(Stored.TryGetValue(hubId, out var s) ? new Versioned<Hub>(s.Hub, s.ETag) : null);

    public Task<IReadOnlyList<Versioned<Hub>>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Versioned<Hub>>>(Stored.Values.Select(s => new Versioned<Hub>(s.Hub, s.ETag)).ToList());

    public Task<bool> TryCreateAsync(Hub hub, CancellationToken cancellationToken)
    {
        RunBeforeWrite();
        if (Stored.ContainsKey(hub.HubId))
        {
            return Task.FromResult(false);
        }

        Put(hub);
        return Task.FromResult(true);
    }

    public Task<bool> TryReplaceAsync(Versioned<Hub> current, Hub updated, CancellationToken cancellationToken)
    {
        RunBeforeWrite();
        if (!Stored.TryGetValue(updated.HubId, out var s) || s.ETag != current.ETag)
        {
            return Task.FromResult(false);
        }

        Put(updated);
        return Task.FromResult(true);
    }

    private void RunBeforeWrite()
    {
        var hook = BeforeWrite;
        BeforeWrite = null; // once
        hook?.Invoke();
    }

    private string NextETag() => $"\"{++version}\"";
}

internal sealed class FakeNotifier : INotifier
{
    public List<Notification> Sent { get; } = [];

    public Exception? FailWith { get; set; }

    public Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (FailWith is { } e)
        {
            throw e;
        }

        Sent.Add(notification);
        return Task.CompletedTask;
    }
}
