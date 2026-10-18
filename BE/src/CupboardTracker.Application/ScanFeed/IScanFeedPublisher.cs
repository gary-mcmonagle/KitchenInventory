namespace CupboardTracker.Application.ScanFeed;

/// <summary>Sends an event to every open app (Azure SignalR in Infrastructure). May throw: use <see cref="ScanFeedNotifier"/>.</summary>
public interface IScanFeedPublisher
{
    Task PublishAsync(ScanFeedEvent feedEvent, CancellationToken cancellationToken);
}
