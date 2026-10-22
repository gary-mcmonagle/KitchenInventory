using CupboardTracker.Application.Products;
using CupboardTracker.Application.Push;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

/// <summary>From app settings (#94): STILL_HERE_ASK_AGAIN_DAYS (default 2), how long "Still here" quiets a unit.</summary>
public sealed record StillGotThisSettings(int AskAgainDays = StillGotThisSettings.DefaultAskAgainDays)
{
    public const int DefaultAskAgainDays = 2;
}

/// <summary>
/// "Still got this?" (#94): which in-stock units ask. A unit asks from its estimated day (estimate ≤ today, UK date)
/// unless someone said it's still here in the last <see cref="StillGotThisSettings.AskAgainDays"/> days (confirmed on
/// today − 2 or earlier asks again). It counts as <b>expired</b> (the push and the sheet) only once the estimate has
/// passed (estimate &lt; today). Units without an estimate never ask.
/// </summary>
public static class StillGotThisRules
{
    public static bool Asks(DateOnly? estimate, DateTimeOffset? lastConfirmedAt, DateOnly today, StockSettings stock, StillGotThisSettings settings) =>
        estimate is { } date && date <= today && !RecentlyConfirmed(lastConfirmedAt, today, stock, settings);

    public static bool IsExpired(DateOnly? estimate, DateTimeOffset? lastConfirmedAt, DateOnly today, StockSettings stock, StillGotThisSettings settings) =>
        estimate is { } date && date < today && !RecentlyConfirmed(lastConfirmedAt, today, stock, settings);

    /// <summary>Confirmed on this UK day: the row shows "Checked" for the rest of it.</summary>
    public static bool ConfirmedToday(DateTimeOffset? lastConfirmedAt, DateOnly today, StockSettings stock) =>
        lastConfirmedAt is { } at && stock.Today(at) == today;

    private static bool RecentlyConfirmed(DateTimeOffset? lastConfirmedAt, DateOnly today, StockSettings stock, StillGotThisSettings settings) =>
        lastConfirmedAt is { } at && stock.Today(at) > today.AddDays(-settings.AskAgainDays);
}

public abstract record StillHereResult
{
    public sealed record Saved(Guid UnitId, Guid ProductId, DateTimeOffset? LastConfirmedAt) : StillHereResult;

    public sealed record NotFound(Guid UnitId) : StillHereResult;

    /// <summary>409: the unit isn't in stock any more.</summary>
    public sealed record NotInStock(Guid UnitId) : StillHereResult;
}

/// <summary>
/// <c>POST /api/units/{unitId}/still-here</c> (#94): sets <c>lastConfirmedAt</c> (via the prompt), so the unit stops
/// asking for a couple of days. Its date is unchanged. Idempotent. <see cref="UndoAsync"/> clears it again.
/// </summary>
public sealed partial class MarkStillHere(IUnitRepository units, TimeProvider clock, ILogger<MarkStillHere> logger)
{
    private const int MaxAttempts = 5;

    public Task<StillHereResult> ExecuteAsync(Guid unitId, CurrentUser user, CancellationToken cancellationToken) =>
        ChangeAsync(unitId, u => u.ConfirmedStillHere(clock.GetUtcNow(), ConfirmedVia.Prompt), "confirmed", user, cancellationToken);

    public Task<StillHereResult> UndoAsync(Guid unitId, CurrentUser user, CancellationToken cancellationToken) =>
        ChangeAsync(unitId, u => u.LastConfirmedAt is null ? null : u.WithoutConfirmation(), "unconfirmed", user, cancellationToken);

    /// <summary><paramref name="change"/> returns null when there's nothing to change.</summary>
    private async Task<StillHereResult> ChangeAsync(
        Guid unitId, Func<Unit, Unit?> change, string what, CurrentUser user, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await units.FindAsync(unitId, cancellationToken);
            if (current is null)
            {
                return new StillHereResult.NotFound(unitId);
            }

            if (current.Value.Status != UnitStatus.InStock)
            {
                return new StillHereResult.NotInStock(unitId);
            }

            if (change(current.Value) is not { } updated)
            {
                return new StillHereResult.Saved(unitId, current.Value.ProductId, current.Value.LastConfirmedAt);
            }

            if (await units.TryReplaceAsync(current, updated, cancellationToken))
            {
                LogChanged(unitId, what, user.ObjectId);
                return new StillHereResult.Saved(unitId, updated.ProductId, updated.LastConfirmedAt);
            }
        }

        throw new InvalidOperationException($"Unit {unitId} kept changing; couldn't save Still here.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Still got this: unit {UnitId} {What} as still here (user {UserId}).")]
    private partial void LogChanged(Guid unitId, string what, Guid userId);
}

/// <summary>An expired unit for the "Still got these?" sheet (#94).</summary>
public sealed record ExpiredUnit(
    Guid UnitId,
    Guid ProductId,
    string Name,
    Storage? Storage,
    DateOnly BoughtOn,
    DateOnly? OpenedOn,
    DateOnly EstimatedExpiryDate,
    int DaysOver);

/// <summary>
/// <c>GET /api/stock/expired</c> (#94): the units that count as expired right now (named, estimate passed, not said to
/// be still here lately), oldest estimate first. Their place is the unit's current storage, else the product's usual one.
/// </summary>
public sealed class GetExpiredUnits(
    IStockQueries queries, IProductRepository products, StockSettings stock, StillGotThisSettings settings, TimeProvider clock)
{
    public async Task<IReadOnlyList<ExpiredUnit>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var today = stock.Today(clock.GetUtcNow());
        var expired = (await queries.GetInStockUnitsAsync(cancellationToken))
            .Where(u => StillGotThisRules.IsExpired(u.EstimatedExpiryDate, u.LastConfirmedAt, today, stock, settings))
            .ToList();
        var ids = expired.Select(u => u.ProductId).Distinct().ToList();
        var found = await Task.WhenAll(ids.Select(id => products.GetAsync(id, cancellationToken)));
        var byId = ids.Zip(found).ToDictionary(p => p.First, p => p.Second);
        return
        [
            .. expired
                .Where(u => byId[u.ProductId] is { Status: ProductStatus.Known })
                .Select(u =>
                {
                    var product = byId[u.ProductId]!;
                    var estimate = u.EstimatedExpiryDate!.Value;
                    return new ExpiredUnit(
                        u.UnitId,
                        u.ProductId,
                        product.Generic?.Name ?? product.DisplayName,
                        u.CurrentStorage ?? product.ShelfLife?.UsualStorage,
                        EstimatedExpiry.BoughtDate(u.CheckedInAt, stock.HomeTimeZone),
                        u.OpenedOn,
                        estimate,
                        -EstimatedExpiry.DaysUntil(estimate, today));
                })
                .OrderBy(u => u.EstimatedExpiryDate)
                .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }
}

/// <summary>Records which days the "Still got this?" push went out (#94), so a re-run never sends twice.</summary>
public interface IStillGotThisPushLog
{
    /// <summary>Claims the day. False if it was already claimed.</summary>
    Task<bool> TryClaimAsync(DateOnly day, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Gives the day back after a failure, so the timer's retry can send.</summary>
    Task ReleaseAsync(DateOnly day, CancellationToken cancellationToken);
}

/// <summary>
/// The "Still got this?" push (#94): at 18:00 UK on Tuesday, Thursday and Sunday, if anything has expired, one push to
/// every device opted in to <see cref="Type"/> (on unless turned off), opening the "Still got these?" sheet. The timer
/// runs at 17:00 and 18:00 UTC on those days (Linux Flex Consumption has no time zone setting) and only the run in the
/// 18:00 hour in the UK acts. Once per day: the day is claimed before sending.
/// </summary>
public sealed partial class SendStillGotThisPush(
    GetExpiredUnits expired, IStillGotThisPushLog log, SendPush send, StockSettings stock, TimeProvider clock,
    ILogger<SendStillGotThisPush> logger)
{
    /// <summary>The preference key on the "This phone" card.</summary>
    public const string Type = "stillGotThis";

    /// <summary>Not "check": Apple refuses topics 1, 5, 9… characters long (#106).</summary>
    public const string Topic = "stillGotThis";

    public const string Path = "/?check=expired";

    public static readonly TimeSpan TimeToLive = TimeSpan.FromHours(6);

    public static readonly IReadOnlySet<DayOfWeek> Days = new HashSet<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Thursday, DayOfWeek.Sunday };

    public const int Hour = 18;

    public static bool IsSendTime(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        return Days.Contains(local.DayOfWeek) && local.Hour == Hour;
    }

    public static PushMessage Message(int count) => new(
        "Cupboard",
        count == 1 ? "1 expired item is still in stock. Still got it?" : $"{count} expired items are still in stock. Still got these?",
        Path,
        Topic,
        TimeToLive);

    public async Task<PushSendSummary?> ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (!IsSendTime(now, stock.HomeTimeZone))
        {
            LogNotSendTime();
            return null;
        }

        var day = stock.Today(now);
        var units = await expired.ExecuteAsync(cancellationToken);
        if (units.Count == 0)
        {
            LogNothingExpired(day);
            return null;
        }

        if (!await log.TryClaimAsync(day, now, cancellationToken))
        {
            LogAlreadySent(day);
            return null;
        }

        try
        {
            var summary = await send.ExecuteAsync(Message(units.Count), new PushAudience { OptedInTo = Type }, cancellationToken);
            LogSent(day, units.Count, summary.Delivered);
            return summary;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await log.ReleaseAsync(day, CancellationToken.None);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Still got this push: not 18:00 on a Tue, Thu or Sun in the UK; nothing to do.")]
    private partial void LogNotSendTime();

    [LoggerMessage(Level = LogLevel.Information, Message = "Still got this push: nothing expired on {Day}; no push.")]
    private partial void LogNothingExpired(DateOnly day);

    [LoggerMessage(Level = LogLevel.Information, Message = "Still got this push: already sent on {Day}.")]
    private partial void LogAlreadySent(DateOnly day);

    [LoggerMessage(Level = LogLevel.Information, Message = "Still got this push: sent on {Day} for {Count} expired units, {Delivered} delivered.")]
    private partial void LogSent(DateOnly day, int count, int delivered);
}
