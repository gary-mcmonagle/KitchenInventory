using CupboardTracker.Application.Adding;
using CupboardTracker.Application.Hubs;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Scanners;

/// <summary>The scanners screen's reads (cross-partition, small).</summary>
public interface IScannersQueries
{
    /// <summary>The scanners (devices with a role; not hubs).</summary>
    Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<UnmatchedCheckOut>> GetUndismissedUnmatchedAsync(CancellationToken cancellationToken);

    /// <summary>Units checked out at or after <paramref name="since"/>, from a scanner or the app.</summary>
    Task<IReadOnlyList<Unit>> GetCheckedOutSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>How often the hub reports in (HEARTBEAT_INTERVAL_SECONDS on the Pi, default 120; the API's copy is only
/// for display).</summary>
public sealed record ScannersSettings(TimeSpan HeartbeatInterval)
{
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(120);
}

public sealed record ScannerHubView(string HubId, DateTimeOffset LastSeenAt, TimeSpan HeartbeatInterval, TimeSpan OfflineThreshold, bool Online);

/// <summary>A scanner: <see cref="Present"/> is null until a heartbeat reports its receiver.</summary>
public sealed record ScannerDeviceView(
    string DeviceId, Role Role, bool? Present, DateTimeOffset? PresentSince, DateTimeOffset? AbsentSince, DateTimeOffset? LastScanAt);

public enum NoMatchKind
{
    NoMatch,
    LookupFailed,

    /// <summary>An in-store printed label waiting for "what is it?" (#58): no product yet.</summary>
    StoreLabel,
}

/// <summary>A check-in to identify: an unnamed product (with its newest in-stock unit) or a store-label check-in.</summary>
public sealed record NoMatchCheckInView(
    Guid? ProductId, string Barcode, NoMatchKind Kind, DateTimeOffset ScannedAt, Guid? UnitId, int InStockCount, Guid? ScanId = null);

/// <summary><see cref="StoreLabel"/>: its barcode is an in-store label, which can never match a unit (#58).</summary>
public sealed record UnmatchedCheckOutView(
    Guid ScanId, string Barcode, Guid? ProductId, string? DisplayName, DateTimeOffset ScannedAt, bool StoreLabel = false);

public sealed record RecentCheckOutView(Guid UnitId, Guid ProductId, string DisplayName, DateTimeOffset CheckedOutAt, string? Source);

public sealed record ScannersView(
    DateTimeOffset GeneratedAt,
    ScannerHubView? Hub,
    IReadOnlyList<ScannerDeviceView> Devices,
    IReadOnlyList<NoMatchCheckInView> NoMatchCheckIns,
    IReadOnlyList<UnmatchedCheckOutView> UnmatchedCheckOuts,
    IReadOnlyList<RecentCheckOutView> RecentlyCheckedOut);

/// <summary>
/// <c>GET /api/scanners</c> (#55): is everything working, and what needs sorting.
/// <list type="bullet">
/// <item>The hub (the most recently heard), online unless silent for longer than the offline alert's threshold.</item>
/// <item>Each scanner, check-in first, with its receiver's presence from the hub's heartbeats (null: never reported).</item>
/// <item>Every product still waiting for a name with something in stock, newest check-in first. Feed dismissals don't
/// hide them here.</item>
/// <item>Every undismissed unmatched check-out, newest first.</item>
/// <item>Units checked out in the last 48 hours, newest first, to undo a mistake.</item>
/// </list>
/// </summary>
public sealed class GetScanners(
    IHubRepository hubs,
    IScannersQueries queries,
    IScanFeedQueries feedQueries,
    IUnitRepository units,
    IProductRepository products,
    IStoreLabelRepository storeLabels,
    OfflineAlertSettings offline,
    ScannersSettings settings,
    TimeProvider clock)
{
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(48);

    public async Task<ScannersView> ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var hub = (await hubs.ListAsync(cancellationToken)).Select(v => v.Value).MaxBy(h => h.LastSeenAt);
        var devices = (await queries.GetDevicesAsync(cancellationToken))
            .OrderBy(d => d.Role == Role.In ? 0 : 1)
            .ThenBy(d => d.DeviceId, StringComparer.Ordinal)
            .Select(d =>
            {
                var state = hub?.Scanners.FirstOrDefault(s => s.DeviceId == d.DeviceId);
                return new ScannerDeviceView(d.DeviceId, d.Role, state?.Present,
                    state is { Present: true } ? state.Since : null, state is { Present: false } ? state.Since : null, d.LastScanAt);
            })
            .ToList();

        var names = new Dictionary<Guid, Product?>();
        async Task<Product?> ProductAsync(Guid id)
        {
            if (!names.TryGetValue(id, out var product))
            {
                names[id] = product = await products.GetAsync(id, cancellationToken);
            }

            return product;
        }

        var noMatch = new List<NoMatchCheckInView>();
        foreach (var product in await feedQueries.GetUnnamedProductsAsync(cancellationToken))
        {
            var inStock = (await units.GetInStockAsync(product.ProductId, cancellationToken)).Select(v => v.Value).ToList();
            if (inStock.MaxBy(u => u.CheckedInAt) is { } newest)
            {
                noMatch.Add(new NoMatchCheckInView(product.ProductId,
                    newest.Barcode ?? (product.Barcodes is [var b, ..] ? b : ""),
                    product.Status == ProductStatus.NoMatch ? NoMatchKind.NoMatch : NoMatchKind.LookupFailed,
                    newest.CheckedInAt, newest.UnitId, inStock.Count));
            }
        }

        // #58: store-label check-ins still waiting for "what is it?" (feed dismissals don't hide them here).
        foreach (var label in await storeLabels.ListUnresolvedAsync(cancellationToken))
        {
            noMatch.Add(new NoMatchCheckInView(null, label.Barcode, NoMatchKind.StoreLabel, label.ScannedAt, null, 1, label.ScanId));
        }

        var unmatched = new List<UnmatchedCheckOutView>();
        foreach (var u in (await queries.GetUndismissedUnmatchedAsync(cancellationToken)).OrderByDescending(u => u.ScannedAt))
        {
            var product = u.ProductId is { } id ? await ProductAsync(id) : null;
            unmatched.Add(new UnmatchedCheckOutView(u.ScanId, u.Barcode, u.ProductId,
                product is { IsUnnamed: false } ? product.DisplayName : null, u.ScannedAt, StoreLabels.IsStoreLabel(u.Barcode)));
        }

        var recent = new List<RecentCheckOutView>();
        foreach (var unit in (await queries.GetCheckedOutSinceAsync(now - RecentWindow, cancellationToken))
            .Where(u => u.Status == UnitStatus.CheckedOut && u.CheckedOutAt is not null)
            .OrderByDescending(u => u.CheckedOutAt))
        {
            var product = await ProductAsync(unit.ProductId);
            recent.Add(new RecentCheckOutView(unit.UnitId, unit.ProductId,
                product is { IsUnnamed: false } ? product.DisplayName : Product.UnknownName(unit.Barcode ?? "no barcode"),
                unit.CheckedOutAt!.Value, unit.CheckOutSource));
        }

        return new ScannersView(
            now,
            hub is null ? null : new ScannerHubView(hub.HubId, hub.LastSeenAt, settings.HeartbeatInterval, offline.Threshold,
                !hub.IsOffline(now, offline.Threshold)),
            devices,
            [.. noMatch.OrderByDescending(n => n.ScannedAt)],
            unmatched,
            recent);
    }
}
