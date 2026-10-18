using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.ScanFeed;

/// <summary>
/// Best-effort publishing for use cases (#54): called after their writes succeed. The feed is only a hint (the API is
/// the source of truth), so a failure is logged as a <b>warning</b> and never fails a scan, its processing, naming or
/// dismissal.
/// </summary>
public sealed partial class ScanFeedNotifier(IScanFeedPublisher publisher, ILogger<ScanFeedNotifier> logger)
{
    public async Task NotifyAsync(ScanFeedEvent feedEvent, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishAsync(feedEvent, cancellationToken);
            LogPublished(feedEvent.Name, Key(feedEvent));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(e, feedEvent.Name, Key(feedEvent));
        }
    }

    private static string Key(ScanFeedEvent feedEvent) => feedEvent switch
    {
        ScanEventBase s => $"scan {s.ScanId}",
        ProductNamed p => $"product {p.ProductId}",
        FeedDismissed d => $"product {d.ProductId}",
        _ => "",
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan feed: sent {EventName} for {Key}.")]
    private partial void LogPublished(string eventName, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scan feed: couldn't send {EventName} for {Key}; the app catches up from the API.")]
    private partial void LogFailed(Exception exception, string eventName, string key);
}
