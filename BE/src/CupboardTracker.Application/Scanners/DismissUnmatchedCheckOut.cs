using CupboardTracker.Application.Stock;
using CupboardTracker.Application.Users;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Scanners;

public abstract record DismissUnmatchedCheckOutResult
{
    public sealed record Dismissed(Guid ScanId) : DismissUnmatchedCheckOutResult;

    public sealed record NotFound(Guid ScanId) : DismissUnmatchedCheckOutResult;
}

/// <summary>Dismisses an unmatched check-out on the scanners screen (#55): reviewed, nothing to do. Idempotent; stock
/// is never changed.</summary>
public sealed partial class DismissUnmatchedCheckOut(
    IUnmatchedCheckOutRepository unmatched, TimeProvider clock, ILogger<DismissUnmatchedCheckOut> logger)
{
    public async Task<DismissUnmatchedCheckOutResult> ExecuteAsync(Guid scanId, CurrentUser user, CancellationToken cancellationToken)
    {
        var record = await unmatched.GetAsync(scanId, cancellationToken);
        if (record is null)
        {
            return new DismissUnmatchedCheckOutResult.NotFound(scanId);
        }

        if (!record.IsDismissed)
        {
            await unmatched.DismissAsync(scanId, user.ObjectId, clock.GetUtcNow(), cancellationToken);
            LogDismissed(scanId, user.ObjectId);
        }

        return new DismissUnmatchedCheckOutResult.Dismissed(scanId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Unmatched check-out {ScanId} dismissed by user {UserId}.")]
    private partial void LogDismissed(Guid scanId, Guid userId);
}
