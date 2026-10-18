using System.Globalization;
using CupboardTracker.Application.ScanFeed;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Infrastructure.SignalR;

/// <summary>
/// The scan feed's JSON (spike #38 contract, docs/api.md): the same shapes go over SignalR and come back from
/// <c>GET /api/scan-feed</c>. Every value is a string, number, bool or null, so any serializer writes the same JSON.
/// </summary>
public static class ScanFeedWire
{
    public static IReadOnlyDictionary<string, object?> Payload(ScanFeedEvent feedEvent)
    {
        var payload = new Dictionary<string, object?>();
        if (feedEvent is ScanEventBase scan)
        {
            payload["scanId"] = scan.ScanId.ToString("D");
            payload["deviceId"] = scan.DeviceId;
            payload["role"] = scan.Role.ToWireName();
        }

        payload["occurredAt"] = Timestamp(feedEvent.OccurredAt);
        switch (feedEvent)
        {
            case ScanReceived r:
                payload["barcode"] = r.Barcode;
                payload["scannedAt"] = Timestamp(r.ScannedAt);
                break;
            case ScanIdentified i:
                payload["outcome"] = i.Outcome == ScanOutcome.CheckedIn ? "checkedIn" : "checkedOut";
                payload["productId"] = i.ProductId.ToString("D");
                payload["displayName"] = i.DisplayName;
                payload["brand"] = i.Brand;
                payload["size"] = i.Size;
                payload["unitId"] = i.UnitId.ToString("D");
                break;
            case ScanNeedsIdentification n:
                payload["productId"] = n.ProductId.ToString("D");
                payload["barcode"] = n.Barcode;
                payload["displayName"] = n.DisplayName;
                payload["productStatus"] = n.ProductStatus == ProductStatus.NoMatch ? "noMatch" : "lookupFailed";
                payload["unitId"] = n.UnitId.ToString("D");
                break;
            case CheckOutUnmatched u:
                payload["barcode"] = u.Barcode;
                payload["productId"] = u.ProductId?.ToString("D");
                payload["displayName"] = u.DisplayName;
                payload["storeLabel"] = StoreLabels.IsStoreLabel(u.Barcode);
                break;
            case StoreLabelScanned l:
                payload["barcode"] = l.Barcode;
                break;
            case StoreLabelResolved r:
                payload["scanId"] = r.ScanId.ToString("D");
                payload["productId"] = r.ProductId.ToString("D");
                payload["displayName"] = r.DisplayName;
                break;
            case ScanSkipped s:
                payload["barcode"] = s.Barcode;
                payload["reason"] = s.Reason;
                break;
            case ProductNamed p:
                payload["productId"] = p.ProductId.ToString("D");
                payload["displayName"] = p.DisplayName;
                break;
            case FeedDismissed d:
                payload["productId"] = d.ProductId?.ToString("D");
                if (d.ScanId is { } scanId)
                {
                    payload["scanId"] = scanId.ToString("D");
                }

                break;
        }

        return payload;
    }

    /// <summary>For <c>GET /api/scan-feed</c>: the payload plus <c>type</c> (the event name).</summary>
    public static IReadOnlyDictionary<string, object?> WithType(ScanFeedEvent feedEvent)
    {
        var item = new Dictionary<string, object?> { ["type"] = feedEvent.Name };
        foreach (var (key, value) in Payload(feedEvent))
        {
            item[key] = value;
        }

        return item;
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
