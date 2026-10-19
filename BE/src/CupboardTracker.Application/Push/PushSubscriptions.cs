using System.Security.Cryptography;
using System.Text;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Push;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Push;

/// <summary>A browser's push subscription as the app sends it (<c>PushSubscription.toJSON()</c>).</summary>
public sealed record PushSubscriptionInput(string? Endpoint, string? P256dh, string? Auth);

public abstract record PushSubscriptionResult
{
    public sealed record Saved(PushSubscription Subscription) : PushSubscriptionResult;

    public sealed record Removed : PushSubscriptionResult;

    public sealed record Invalid(string Error) : PushSubscriptionResult;

    public sealed record NotFound : PushSubscriptionResult;
}

/// <summary>Ids, validation and device labels for push subscriptions (#77).</summary>
public static class PushSubscriptionRules
{
    public const int MaxPreferenceTypes = 20;

    /// <summary>The document id: a hash of the endpoint, so the same subscription always lands on the same document.</summary>
    public static string IdFor(string endpoint) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? Problem(PushSubscriptionInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Endpoint) || input.Endpoint.Length > 2048 || !PushServices.IsAllowed(input.Endpoint))
        {
            return "endpoint must be a push service URL.";
        }

        if (Decode(input.P256dh) is not { Length: 65 } key || key[0] != 0x04)
        {
            return "keys.p256dh must be a P-256 public key (base64url).";
        }

        return Decode(input.Auth) is { Length: 16 } ? null : "keys.auth must be 16 bytes (base64url).";
    }

    /// <summary>A short, human label for the device, from its user agent ("iPhone", "Android", "Mac", ...).</summary>
    public static string DeviceLabel(string? userAgent)
    {
        var ua = userAgent ?? "";
        return ua.Contains("iPhone", StringComparison.Ordinal) ? "iPhone"
            : ua.Contains("iPad", StringComparison.Ordinal) ? "iPad"
            : ua.Contains("Android", StringComparison.Ordinal) ? "Android"
            : ua.Contains("Macintosh", StringComparison.Ordinal) ? "Mac"
            : ua.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : ua.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : "Browser";
    }

    public static bool IsPreferenceType(string type) => PushTopic.IsValid(type);

    private static byte[]? Decode(string? base64Url)
    {
        if (string.IsNullOrEmpty(base64Url) || base64Url.Length > 200)
        {
            return null;
        }

        var s = base64Url.Replace('-', '+').Replace('_', '/');
        try
        {
            return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary><c>PUT /api/push/subscriptions</c> (#77): saves this device's subscription for the caller, or refreshes it
/// (the app does on every open, which keeps <c>lastSeenAt</c> fresh). Preferences already set are kept.</summary>
public sealed partial class RegisterPushSubscription(
    IPushSubscriptionRepository repository, TimeProvider clock, ILogger<RegisterPushSubscription> logger)
{
    public async Task<PushSubscriptionResult> ExecuteAsync(
        PushSubscriptionInput input, string? userAgent, CurrentUser user, CancellationToken cancellationToken)
    {
        if (PushSubscriptionRules.Problem(input) is { } problem)
        {
            return new PushSubscriptionResult.Invalid(problem);
        }

        var now = clock.GetUtcNow();
        var id = PushSubscriptionRules.IdFor(input.Endpoint!);
        var existing = await repository.GetAsync(user.ObjectId, id, cancellationToken);
        var subscription = new PushSubscription(
            id, user.ObjectId, input.Endpoint!, input.P256dh!, input.Auth!, PushSubscriptionRules.DeviceLabel(userAgent),
            existing?.CreatedAt ?? now, now, existing?.LastSuccessAt, existing?.Preferences ?? new Dictionary<string, bool>());
        await repository.UpsertAsync(subscription, cancellationToken);
        if (existing is null)
        {
            LogRegistered(subscription.DeviceLabel, new Uri(subscription.Endpoint).Host);
        }

        return new PushSubscriptionResult.Saved(subscription);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push subscription added: {Device} via {PushService}.")]
    private partial void LogRegistered(string device, string pushService);
}

/// <summary><c>DELETE /api/push/subscriptions</c> (#77): removes one of the caller's own subscriptions.</summary>
public sealed class RemovePushSubscription(IPushSubscriptionRepository repository)
{
    public async Task<PushSubscriptionResult> ExecuteAsync(string? endpoint, CurrentUser user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new PushSubscriptionResult.Invalid("endpoint is required.");
        }

        return await repository.DeleteAsync(user.ObjectId, PushSubscriptionRules.IdFor(endpoint), cancellationToken)
            ? new PushSubscriptionResult.Removed()
            : new PushSubscriptionResult.NotFound();
    }
}

/// <summary><c>PATCH /api/push/subscriptions/preferences</c> (#77): which notification types this device wants. Only
/// the caller's own subscription; types are letters and digits (the features name them, e.g. <c>stockChanges</c>).</summary>
public sealed class SetPushPreferences(IPushSubscriptionRepository repository)
{
    public async Task<PushSubscriptionResult> ExecuteAsync(
        string? endpoint, IReadOnlyDictionary<string, bool> preferences, CurrentUser user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new PushSubscriptionResult.Invalid("endpoint is required.");
        }

        if (preferences.Count is 0 or > PushSubscriptionRules.MaxPreferenceTypes || !preferences.Keys.All(PushSubscriptionRules.IsPreferenceType))
        {
            return new PushSubscriptionResult.Invalid("preferences must map 1 to 20 types (letters and digits) to true or false.");
        }

        var existing = await repository.GetAsync(user.ObjectId, PushSubscriptionRules.IdFor(endpoint), cancellationToken);
        if (existing is null)
        {
            return new PushSubscriptionResult.NotFound();
        }

        var merged = existing.Preferences.ToDictionary();
        foreach (var (type, on) in preferences)
        {
            merged[type] = on;
        }

        if (merged.Count > PushSubscriptionRules.MaxPreferenceTypes)
        {
            return new PushSubscriptionResult.Invalid("Too many notification types.");
        }

        var updated = existing with { Preferences = merged };
        await repository.UpsertAsync(updated, cancellationToken);
        return new PushSubscriptionResult.Saved(updated);
    }
}

/// <summary>The daily tidy-up (#77): subscriptions not refreshed by their app for 30 days are deleted. Closed but not
/// unsubscribed browsers keep accepting pushes (spike #75), so this is how they go.</summary>
public sealed partial class ExpireStalePushSubscriptions(
    IPushSubscriptionRepository repository, TimeProvider clock, ILogger<ExpireStalePushSubscriptions> logger)
{
    public static readonly TimeSpan MaxUnseen = TimeSpan.FromDays(30);

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - MaxUnseen;
        var removed = 0;
        foreach (var stale in (await repository.ListAsync(cancellationToken)).Where(s => s.LastSeenAt < cutoff))
        {
            if (await repository.DeleteAsync(stale.UserId, stale.Id, cancellationToken))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            LogExpired(removed);
        }

        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} push subscriptions not seen for 30 days.")]
    private partial void LogExpired(int count);
}
