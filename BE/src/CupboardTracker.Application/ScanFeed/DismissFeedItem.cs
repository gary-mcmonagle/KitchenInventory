using CupboardTracker.Application.Products;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.ScanFeed;

public abstract record DismissFeedItemResult
{
    public sealed record Dismissed(Guid ProductId, DateTimeOffset At) : DismissFeedItemResult;

    public sealed record NotFound(Guid ProductId) : DismissFeedItemResult;
}

/// <summary>
/// Dismisses a product's amber "No match" toast for the whole household (#54): records <c>feedDismissedAt</c> and tells
/// every open app. It changes nothing else: stock, status and "Waiting for a name" stay as they are. A newer
/// check-in of the product shows a fresh toast.
/// </summary>
public sealed partial class DismissFeedItem(
    IProductRepository products, ScanFeedNotifier feed, TimeProvider clock, ILogger<DismissFeedItem> logger)
{
    private const int MaxRounds = 3;

    public async Task<DismissFeedItemResult> ExecuteAsync(Guid productId, CurrentUser user, CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxRounds; round++)
        {
            var current = await products.GetVersionedAsync(productId, cancellationToken);
            if (current is null)
            {
                return new DismissFeedItemResult.NotFound(productId);
            }

            var at = clock.GetUtcNow();
            if (await products.TryReplaceAsync(current, current.Value.DismissedFromFeed(at), cancellationToken))
            {
                LogDismissed(productId, user.ObjectId);
                await feed.NotifyAsync(new FeedDismissed(productId, at), cancellationToken);
                return new DismissFeedItemResult.Dismissed(productId, at);
            }
        }

        throw new InvalidOperationException($"Couldn't dismiss product {productId} after {MaxRounds} conflicting writes.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Product {ProductId} dismissed from the scan feed by user {UserId}.")]
    private partial void LogDismissed(Guid productId, Guid userId);
}
