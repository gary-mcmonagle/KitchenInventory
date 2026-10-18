using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.ScanFeed;

/// <summary>
/// A live scan feed event (#54, spike #38's contract: add fields, never rename). Scan events are keyed by scan ID;
/// <see cref="ProductNamed"/> and <see cref="FeedDismissed"/> by product. No personal data.
/// </summary>
public abstract record ScanFeedEvent(DateTimeOffset OccurredAt)
{
    /// <summary>The client method name: <c>connection.on("scanReceived", …)</c>.</summary>
    public abstract string Name { get; }
}

/// <summary>A scan event's common fields.</summary>
public abstract record ScanEventBase(Guid ScanId, string DeviceId, Role Role, DateTimeOffset OccurredAt) : ScanFeedEvent(OccurredAt);

/// <summary>Ingest stored the scan (grey toast).</summary>
public sealed record ScanReceived(Guid ScanId, string DeviceId, Role Role, DateTimeOffset OccurredAt, string Barcode, DateTimeOffset ScannedAt)
    : ScanEventBase(ScanId, DeviceId, Role, OccurredAt)
{
    public override string Name => "scanReceived";
}

public enum ScanOutcome
{
    CheckedIn,
    CheckedOut,
}

/// <summary>A known product checked in or out (green toast).</summary>
public sealed record ScanIdentified(
    Guid ScanId, string DeviceId, Role Role, DateTimeOffset OccurredAt, ScanOutcome Outcome,
    Guid ProductId, string DisplayName, string? Brand, string? Size, Guid UnitId)
    : ScanEventBase(ScanId, DeviceId, Role, OccurredAt)
{
    public override string Name => "scanIdentified";
}

/// <summary>A check-in of a product with no name yet (amber toast, Identify).</summary>
public sealed record ScanNeedsIdentification(
    Guid ScanId, string DeviceId, DateTimeOffset OccurredAt, Guid ProductId, string Barcode, string DisplayName,
    ProductStatus ProductStatus, Guid UnitId)
    : ScanEventBase(ScanId, DeviceId, Role.In, OccurredAt)
{
    public override string Name => "scanNeedsIdentification";
}

/// <summary>A check-out with nothing in stock to take (amber "Not in stock" toast).</summary>
public sealed record CheckOutUnmatched(
    Guid ScanId, string DeviceId, DateTimeOffset OccurredAt, string Barcode, Guid? ProductId, string? DisplayName)
    : ScanEventBase(ScanId, DeviceId, Role.Out, OccurredAt)
{
    public override string Name => "checkOutUnmatched";
}

/// <summary>A scan that isn't processed yet (store labels): the grey toast goes.</summary>
public sealed record ScanSkipped(Guid ScanId, string DeviceId, Role Role, DateTimeOffset OccurredAt, string Barcode, string Reason)
    : ScanEventBase(ScanId, DeviceId, Role, OccurredAt)
{
    public const string StoreLabel = "storeLabel";

    public override string Name => "scanSkipped";
}

/// <summary>An unknown item was named (#51): its amber toasts go (new in #54).</summary>
public sealed record ProductNamed(Guid ProductId, string DisplayName, DateTimeOffset OccurredAt) : ScanFeedEvent(OccurredAt)
{
    public override string Name => "productNamed";
}

/// <summary>Someone dismissed a product's amber toast: it goes on every open app (new in #54).</summary>
public sealed record FeedDismissed(Guid ProductId, DateTimeOffset OccurredAt) : ScanFeedEvent(OccurredAt)
{
    public override string Name => "feedDismissed";
}
