using CupboardTracker.Domain.Push;

namespace CupboardTracker.Application.Push;

/// <summary>One device's push subscription (#77), stored per person (Entra <c>oid</c>) and device.</summary>
public sealed record PushSubscription(
    string Id,
    Guid UserId,
    string Endpoint,
    string P256dh,
    string Auth,
    string DeviceLabel,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastSuccessAt,
    IReadOnlyDictionary<string, bool> Preferences)
{
    /// <summary>Opted in to a type: a type the features haven't set yet counts as off.</summary>
    public bool Wants(string type) => Preferences.TryGetValue(type, out var on) && on;
}

public enum PushUrgency
{
    VeryLow,
    Low,
    Normal,
    High,
}

/// <summary>
/// What a push shows (Declarative Web Push, spike #75): a title, a body, where tapping opens (a path in the app), and
/// a topic that's also the notification tag, so a newer push of the same kind replaces an older one.
/// </summary>
public sealed record PushMessage(string Title, string Body, string Path, string Topic, TimeSpan TimeToLive, PushUrgency Urgency = PushUrgency.Normal)
{
    public static string? Problem(PushMessage message) =>
        !PushTopic.IsValid(message.Topic) ? "The topic must be letters and digits only, at most 32, and not 1, 5, 9… characters long (Apple reads it as Base64)."
        : !message.Path.StartsWith('/') || message.Path.StartsWith("//", StringComparison.Ordinal) ? "The path must be a path in the app."
        : string.IsNullOrWhiteSpace(message.Title) ? "A push needs a title."
        : message.TimeToLive <= TimeSpan.Zero ? "The time to live must be positive."
        : null;
}

/// <summary>Who a push goes to (#77). Every filter given applies.</summary>
public sealed record PushAudience
{
    /// <summary>Only subscriptions opted in to this notification type (a key in their preferences).</summary>
    public string? OptedInTo { get; init; }

    /// <summary>Only these people (any of their devices).</summary>
    public IReadOnlyList<Guid>? OnlyUsers { get; init; }

    /// <summary>Not these people.</summary>
    public IReadOnlyList<Guid>? ExceptUsers { get; init; }

    /// <summary>Not people who sent a "seen" receipt for this event from any of their devices.</summary>
    public string? ExceptSeenEventKey { get; init; }
}

public enum PushOutcome
{
    Delivered,

    /// <summary>The push service says the subscription no longer exists (404 or 410): delete it.</summary>
    Gone,

    /// <summary>Throttled or a server error after the sender's own retries.</summary>
    Failed,

    /// <summary>Refused as invalid (400, 403, 413): a bug or a configuration problem.</summary>
    Refused,

    /// <summary>No VAPID keys configured (e.g. the Key Vault secret isn't set yet).</summary>
    NotConfigured,
}

/// <summary>Sends one push to one subscription (our own Web Push sender in Infrastructure: aes128gcm and VAPID).</summary>
public interface IPushSender
{
    Task<PushOutcome> SendAsync(PushSubscription subscription, PushMessage message, CancellationToken cancellationToken);
}

/// <summary>The pushSubscriptions container (partition /userId).</summary>
public interface IPushSubscriptionRepository
{
    Task<PushSubscription?> GetAsync(Guid userId, string id, CancellationToken cancellationToken);

    Task UpsertAsync(PushSubscription subscription, CancellationToken cancellationToken);

    /// <summary>False if there was nothing to delete.</summary>
    Task<bool> DeleteAsync(Guid userId, string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PushSubscription>> ListAsync(CancellationToken cancellationToken);

    Task MarkDeliveredAsync(Guid userId, string id, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>The pushSeen container (partition /eventKey, documents expire after about a day): who saw what, and
/// which push jobs have already run.</summary>
public interface IPushSeenRepository
{
    Task RecordAsync(Guid userId, IReadOnlyCollection<string> eventKeys, DateTimeOffset at, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> UsersWhoSawAsync(string eventKey, CancellationToken cancellationToken);

    /// <summary>Claims a push job so a re-delivered queue message never sends twice. False if it was already claimed.</summary>
    Task<bool> TryClaimJobAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>A push to send later (#77): a Storage queue message with an initial visibility delay in Infrastructure.</summary>
public sealed record PushJob(Guid JobId, PushMessage Message, PushAudience Audience);

public interface IPushJobScheduler
{
    Task ScheduleAsync(PushJob job, TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>From app settings: the VAPID public key the browser subscribes with (the private key stays in Infrastructure).</summary>
public sealed record PushSettings(string? PublicKey)
{
    public bool IsConfigured => !string.IsNullOrEmpty(PublicKey);
}
