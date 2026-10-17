namespace CupboardTracker.Application.Notifications;

/// <summary>Sends a notification by email. Who receives each kind is infrastructure config, never use-case logic
/// (spike #39). Throws if it can't send, after the provider's own retries.</summary>
public interface INotifier
{
    Task SendAsync(Notification notification, CancellationToken cancellationToken);
}

public sealed record Notification(NotificationKind Kind, EmailContent Content);

public sealed record EmailContent(string Subject, string Html, string PlainText);

public enum NotificationKind
{
    OfflineAlert,
}
