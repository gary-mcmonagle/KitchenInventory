using CupboardTracker.Application.Notifications;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Digest;

/// <summary>
/// Sends the daily digest from the timer run in the 21:00 hour UK time, once per day: the day is claimed before
/// sending (a second run finds the claim and sends nothing), and released if anything fails so the retry can send.
/// </summary>
public sealed partial class SendDailyDigest(
    IDigestQueries queries,
    IProductRepository products,
    ISentDigestRepository sentDigests,
    INotifier notifier,
    DigestSettings settings,
    StockSettings stock,
    TimeProvider clock,
    ILogger<SendDailyDigest> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var zone = settings.HomeTimeZone;
        if (!DigestWindow.IsSendTime(now, zone))
        {
            LogNotSendTime(TimeZoneInfo.ConvertTime(now, zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        var window = DigestWindow.EndingOn(DigestWindow.LocalDate(now, zone), zone);
        if (!await sentDigests.TryClaimAsync(window, now, cancellationToken))
        {
            LogAlreadySent(window.Date);
            return;
        }

        try
        {
            var data = await GatherAsync(window, cancellationToken);
            await notifier.SendAsync(
                new Notification(NotificationKind.DailyDigest, BuildDailyDigest.Build(data)), cancellationToken);
            LogSent(window.Date, data.CheckedInCount, data.CheckedOutCount, data.InStock.Sum(l => l.Count),
                data.UnknownInStock.Sum(l => l.Count), window.Start, window.End);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(e, window.Date);
            await sentDigests.ReleaseAsync(window.Date, CancellationToken.None);
            throw; // the timer's retry tries again
        }
    }

    public async Task<DigestData> GatherAsync(DigestWindow window, CancellationToken cancellationToken)
    {
        var inStock = await queries.GetInStockAsync(cancellationToken);
        var checkedIn = await queries.GetCheckedInAsync(window, cancellationToken);
        var checkedOut = await queries.GetCheckedOutAsync(window, cancellationToken);

        var known = new Dictionary<Guid, Product?>();
        foreach (var id in inStock.Concat(checkedIn).Concat(checkedOut).Select(u => u.ProductId).Distinct())
        {
            known[id] = await products.GetAsync(id, cancellationToken);
        }

        bool IsNamed(DigestUnit unit) => known[unit.ProductId] is { Status: ProductStatus.Known };
        string Name(DigestUnit unit) => IsNamed(unit)
            ? known[unit.ProductId]!.DisplayName
            : Product.UnknownName(unit.Barcode ?? (known[unit.ProductId]?.Barcodes is [var first, ..] ? first : null) ?? "no barcode");

        return new DigestData(
            window.Date,
            Group(checkedIn, Name),
            Group(checkedOut, Name),
            Group(inStock.Where(IsNamed), Name),
            Group(inStock.Where(u => !IsNamed(u)), Name),
            UseFirst(inStock.Where(IsNamed), Name, window.Date));
    }

    /// <summary>Named products whose soonest unit is expired or due within the Use first threshold on the digest's
    /// day, soonest first, then by name.</summary>
    private List<DigestUseFirstLine> UseFirst(IEnumerable<DigestUnit> named, Func<DigestUnit, string> name, DateOnly today) =>
        named.GroupBy(u => u.ProductId)
            .Select(g => (Name: name(g.First()), Count: g.Count(), Soonest: g.Min(u => u.EstimatedExpiryDate),
                Note: g.Where(u => u.EstimatedExpiryDate is not null).MinBy(u => u.EstimatedExpiryDate)?.StateNote))
            .Where(p => p.Soonest is { } soonest && stock.IsUseFirst(EstimatedExpiry.DaysUntil(soonest, today)))
            .OrderBy(p => p.Soonest)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new DigestUseFirstLine(p.Name, p.Count, ExpiryBadge.Text(p.Soonest!.Value, today), p.Note))
            .ToList();

    /// <summary>Counts per name, alphabetical ignoring case.</summary>
    public static IReadOnlyList<DigestLine> Group(IEnumerable<DigestUnit> units, Func<DigestUnit, string> name) =>
        units.GroupBy(name)
            .Select(g => new DigestLine(g.Key, g.Count()))
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToList();

    [LoggerMessage(Level = LogLevel.Information, Message = "Not the digest hour in the UK ({LocalTime}); no digest from this run.")]
    private partial void LogNotSendTime(string localTime);

    [LoggerMessage(Level = LogLevel.Information, Message = "The digest for {Date} was already sent; nothing to do.")]
    private partial void LogAlreadySent(DateOnly date);

    [LoggerMessage(Level = LogLevel.Information, Message = "Daily digest for {Date} sent: {In} in, {Out} out, {InStock} in stock, {Unknown} unknown (window {Start:o} to {End:o}).")]
    private partial void LogSent(DateOnly date, int @in, int @out, int inStock, int unknown, DateTimeOffset start, DateTimeOffset end);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't send the daily digest for {Date}; it will be retried.")]
    private partial void LogFailed(Exception exception, DateOnly date);
}
