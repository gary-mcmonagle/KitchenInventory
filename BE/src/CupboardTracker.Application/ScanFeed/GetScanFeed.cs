using CupboardTracker.Application.Products;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.ScanFeed;

/// <summary>The catch-up reads (cross-partition; a few minutes of scans and a handful of products).</summary>
public interface IScanFeedQueries
{
    /// <summary>Scan events received at or after <paramref name="since"/>, oldest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<ScanEvent>> GetScansSinceAsync(DateTimeOffset since, int max, CancellationToken cancellationToken);

    /// <summary>Units created by these scans (id = check-in scan ID) or checked out by them.</summary>
    Task<IReadOnlyList<Unit>> GetUnitsForScansAsync(IReadOnlyCollection<Guid> scanIds, CancellationToken cancellationToken);

    /// <summary>Unmatched check-outs recorded for these scans.</summary>
    Task<IReadOnlyList<UnmatchedCheckOut>> GetUnmatchedAsync(IReadOnlyCollection<Guid> scanIds, CancellationToken cancellationToken);

    /// <summary>Products with no name yet (no match, or lookup failed).</summary>
    Task<IReadOnlyList<Product>> GetUnnamedProductsAsync(CancellationToken cancellationToken);
}

public sealed record ScanFeedView(DateTimeOffset GeneratedAt, IReadOnlyList<ScanFeedEvent> Events);

/// <summary>
/// <c>GET /api/scan-feed</c> (#54): what the app missed, as feed events keyed by scan ID, merged in by the app after
/// every (re)connect and on load. Two parts:
/// <list type="bullet">
/// <item>each scan received since <c>since</c>, with its outcome now (still reading, identified, needs naming, not in
/// stock, or skipped);</item>
/// <item>every product still waiting for a name with something in stock, whose newest check-in is after its last
/// dismissal, so amber toasts survive a reload.</item>
/// </list>
/// </summary>
public sealed class GetScanFeed(
    IScanFeedQueries queries, IProductRepository products, IUnitRepository units, TimeProvider clock)
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(24);
    public const int MaxScans = 200;

    public async Task<ScanFeedView> ExecuteAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var from = since ?? now - DefaultWindow;
        if (from < now - MaxWindow)
        {
            from = now - MaxWindow;
        }

        var events = new Dictionary<Guid, ScanFeedEvent>();
        var unnamed = (await queries.GetUnnamedProductsAsync(cancellationToken)).ToDictionary(p => p.ProductId);
        await AddRecentAsync(from, events, unnamed, cancellationToken);
        await AddWaitingForNameAsync(events, unnamed, cancellationToken);
        return new ScanFeedView(now, [.. events.Values.OrderBy(e => e.OccurredAt)]);
    }

    private async Task AddRecentAsync(
        DateTimeOffset from, Dictionary<Guid, ScanFeedEvent> events, Dictionary<Guid, Product> unnamed, CancellationToken cancellationToken)
    {
        var scans = await queries.GetScansSinceAsync(from, MaxScans, cancellationToken);
        if (scans.Count == 0)
        {
            return;
        }

        var ids = scans.Select(s => s.ScanId).ToList();
        var related = await queries.GetUnitsForScansAsync(ids, cancellationToken);
        var checkedIn = related.Where(u => u.CheckInScanId is not null).ToDictionary(u => u.UnitId);
        var checkedOut = related.Where(u => u.CheckOutScanId is not null).ToDictionary(u => u.CheckOutScanId!.Value);
        var unmatched = (await queries.GetUnmatchedAsync(ids, cancellationToken)).ToDictionary(u => u.ScanId);
        var named = new Dictionary<Guid, Product?>();

        async Task<Product?> ProductAsync(Guid id)
        {
            if (unnamed.TryGetValue(id, out var p))
            {
                return p;
            }

            if (!named.TryGetValue(id, out var found))
            {
                named[id] = found = await products.GetAsync(id, cancellationToken);
            }

            return found;
        }

        foreach (var scan in scans)
        {
            var at = scan.ReceivedAt;
            ScanFeedEvent? outcome = null;
            if (StoreLabels.IsStoreLabel(scan.Barcode))
            {
                outcome = new ScanSkipped(scan.ScanId, scan.DeviceId, scan.Role, at, scan.Barcode.Value, ScanSkipped.StoreLabel);
            }
            else if (scan.Role == Role.In && checkedIn.TryGetValue(scan.ScanId, out var unit))
            {
                var product = await ProductAsync(unit.ProductId);
                if (product is { IsUnnamed: true })
                {
                    // Dismissed since (or the newest check-in of it is reported below): no amber toast for this one.
                    if (product.FeedDismissedAt is { } dismissed && dismissed >= scan.ReceivedAt)
                    {
                        continue;
                    }

                    outcome = new ScanNeedsIdentification(scan.ScanId, scan.DeviceId, at, product.ProductId,
                        scan.Barcode.Value, product.DisplayName, product.Status, unit.UnitId);
                }
                else if (product is not null)
                {
                    outcome = new ScanIdentified(scan.ScanId, scan.DeviceId, scan.Role, at, ScanOutcome.CheckedIn,
                        product.ProductId, product.DisplayName, product.Details?.Brand, product.Details?.Size, unit.UnitId);
                }
            }
            else if (scan.Role == Role.Out && unmatched.TryGetValue(scan.ScanId, out var none))
            {
                var product = none.ProductId is { } pid ? await ProductAsync(pid) : null;
                outcome = new CheckOutUnmatched(scan.ScanId, scan.DeviceId, at, scan.Barcode.Value, none.ProductId,
                    product is { IsUnnamed: false } ? product.DisplayName : null);
            }
            else if (scan.Role == Role.Out && checkedOut.TryGetValue(scan.ScanId, out var taken))
            {
                var product = await ProductAsync(taken.ProductId);
                outcome = new ScanIdentified(scan.ScanId, scan.DeviceId, scan.Role, at, ScanOutcome.CheckedOut,
                    taken.ProductId, product?.DisplayName ?? Product.UnknownName(scan.Barcode.Value),
                    product?.Details?.Brand, product?.Details?.Size, taken.UnitId);
            }

            // Not processed yet: still "Reading…".
            events[scan.ScanId] = outcome ?? new ScanReceived(scan.ScanId, scan.DeviceId, scan.Role, at, scan.Barcode.Value, scan.ScannedAt);
        }
    }

    private async Task AddWaitingForNameAsync(
        Dictionary<Guid, ScanFeedEvent> events, Dictionary<Guid, Product> unnamed, CancellationToken cancellationToken)
    {
        foreach (var product in unnamed.Values)
        {
            var newest = (await units.GetInStockAsync(product.ProductId, cancellationToken))
                .Select(v => v.Value).MaxBy(u => u.CheckedInAt);
            if (newest is null || (product.FeedDismissedAt is { } dismissed && dismissed >= newest.CheckedInAt))
            {
                continue; // nothing in stock, or dismissed since its newest check-in
            }

            var scanId = newest.CheckInScanId ?? newest.UnitId;
            if (events.Values.OfType<ScanNeedsIdentification>().Any(e => e.ProductId == product.ProductId))
            {
                continue; // already reported from a recent scan
            }

            events[scanId] = new ScanNeedsIdentification(scanId, newest.CheckInDeviceId ?? "app", newest.CheckedInAt,
                product.ProductId, newest.Barcode ?? (product.Barcodes is [var first, ..] ? first : ""), product.DisplayName,
                product.Status, newest.UnitId);
        }
    }
}
