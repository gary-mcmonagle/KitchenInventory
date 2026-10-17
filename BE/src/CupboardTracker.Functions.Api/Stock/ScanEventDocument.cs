using System.Globalization;
using System.Text.Json.Serialization;
using CupboardTracker.Domain;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>A scan event as stored by ingest (docs/data-model.md), read from the change feed.</summary>
public sealed record ScanEventDocument(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("barcode")] string? Barcode,
    [property: JsonPropertyName("deviceId")] string? DeviceId,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("scannedAt")] string? ScannedAt,
    [property: JsonPropertyName("receivedAt")] string? ReceivedAt,
    [property: JsonPropertyName("clientSha")] string? ClientSha)
{
    /// <summary>Null if the stored document isn't a valid scan event (ingest validates, so this shouldn't happen).</summary>
    public ScanEvent? ToScanEvent()
    {
        if (!Guid.TryParse(Id, out var scanId)
            || !Domain.Barcode.TryCreate(Barcode, out var barcode, out _)
            || !DeviceIds.IsValid(DeviceId)
            || !RoleNames.TryParse(Role, out var role)
            || !DateTimeOffset.TryParse(ScannedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var scannedAt))
        {
            return null;
        }

        var receivedAt = DateTimeOffset.TryParse(
            ReceivedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r) ? r : scannedAt;
        return new ScanEvent(scanId, barcode!, DeviceId!, role, scannedAt, receivedAt, ClientSha ?? "");
    }
}
