using Azure.Identity;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Infrastructure.ShoppingList;
using Microsoft.Azure.SignalR;
using Microsoft.Azure.SignalR.Management;
using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure.SignalR;

/// <summary>Where the scan feed's SignalR Service is (#54), from app settings; no secrets.</summary>
/// <param name="ServiceUri">Azure: <c>AzureSignalRConnectionString__serviceUri</c>, used with the app's managed identity.</param>
/// <param name="ConnectionString">Local development only: the SignalR emulator's connection string in
/// <c>AzureSignalRConnectionString</c> (git-ignored local.settings.json).</param>
public sealed record SignalROptions(Uri? ServiceUri, string? ConnectionString)
{
    public const string HubName = "ScanFeed";

    public bool IsConfigured => ServiceUri is not null || ConnectionString is not null;

    public static SignalROptions FromConfiguration(IConfiguration config) => new(
        Uri.TryCreate(config["AzureSignalRConnectionString:serviceUri"], UriKind.Absolute, out var uri) ? uri : null,
        config["AzureSignalRConnectionString"] is { Length: > 0 } cs ? cs : null);

    /// <summary>Never shows a connection string.</summary>
    public override string ToString() => $"SignalROptions {{ ServiceUri = {ServiceUri}, Emulator = {ConnectionString is not null} }}";
}

/// <summary>A client connection for the React app: where to connect, and the SignalR access token.</summary>
public sealed record ScanFeedConnection(string Url, string AccessToken);

/// <summary>
/// The scan feed over Azure SignalR (spike #38): the Management SDK in <b>transient</b> mode (REST calls, so no server
/// connections count against the Free tier's 20), authenticated with the app's managed identity. Sends every event to
/// every connection (all signed-in household members), and negotiates client connections for the API app.
/// Singleton: the hub context is created once and reused.
/// </summary>
/// The shopping list (#84) uses the same hub, so one connection per open app carries both.
public sealed class SignalRScanFeed(SignalROptions options) : IScanFeedPublisher, IShoppingListPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ServiceHubContext? hub;

    public async Task PublishAsync(ScanFeedEvent feedEvent, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return; // not set up (e.g. local without the emulator): the app still catches up from the API
        }

        var context = await HubAsync(cancellationToken);
        await context.Clients.All.SendCoreAsync(feedEvent.Name, [ScanFeedWire.Payload(feedEvent)], cancellationToken);
    }

    public Task ItemChangedAsync(ShoppingListItem item, CancellationToken cancellationToken) =>
        SendAsync("shoppingListItemChanged", ShoppingListWire.Item(item), cancellationToken);

    public Task ItemRemovedAsync(Guid id, long version, CancellationToken cancellationToken) =>
        SendAsync("shoppingListItemRemoved", new Dictionary<string, object?> { ["id"] = id.ToString("D"), ["version"] = version }, cancellationToken);

    public Task ClearedAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        SendAsync("shoppingListCleared", new Dictionary<string, object?> { ["ids"] = ids.Select(i => i.ToString("D")).ToArray() }, cancellationToken);

    private async Task SendAsync(string name, object payload, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return; // not set up: apps catch up from the API
        }

        var context = await HubAsync(cancellationToken);
        await context.Clients.All.SendCoreAsync(name, [payload], cancellationToken);
    }

    public async Task<ScanFeedConnection> NegotiateAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException("The scan feed's SignalR Service isn't configured.");
        }

        var context = await HubAsync(cancellationToken);
        var response = await context.NegotiateAsync(new NegotiationOptions { UserId = userId.ToString("D") }, cancellationToken);
        return new ScanFeedConnection(response.Url!, response.AccessToken!);
    }

    private async Task<ServiceHubContext> HubAsync(CancellationToken cancellationToken)
    {
        if (hub is not null)
        {
            return hub;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (hub is null)
            {
                using var manager = new ServiceManagerBuilder()
                    .WithOptions(o =>
                    {
                        if (options.ServiceUri is { } uri)
                        {
                            o.ServiceEndpoints = [new ServiceEndpoint(uri, new DefaultAzureCredential())];
                        }
                        else
                        {
                            o.ConnectionString = options.ConnectionString;
                        }

                        o.ServiceTransportType = ServiceTransportType.Transient;
                    })
                    .BuildServiceManager();
                hub = await manager.CreateHubContextAsync(SignalROptions.HubName, cancellationToken);
            }

            return hub;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (hub is not null)
        {
            await hub.DisposeAsync();
        }

        gate.Dispose();
    }
}
