using System.Globalization;
using System.Text.Json;
using CupboardTracker.Domain;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Scans;

/// <summary>A scan event as sent by the client, before validation. See docs/api.md.</summary>
public sealed record RecordScanCommand(
    string? ScanId,
    string? Barcode,
    string? DeviceId,
    string? Role,
    string? ScannedAt,
    string? ClientSha,
    IReadOnlyDictionary<string, JsonElement> ExtraFields);

public abstract record RecordScanResult
{
    /// <summary>Stored, or already stored (a resend): both are success.</summary>
    public sealed record Recorded(Guid ScanId, bool WasAlreadyRecorded) : RecordScanResult;

    public sealed record Invalid(IReadOnlyList<string> Errors) : RecordScanResult;
}

/// <summary>
/// Records a scan event exactly once (the scan ID is the document id, so resends are no-ops),
/// and creates or updates the scanning device with its role.
/// </summary>
public sealed partial class RecordScan(
    IScanEventRepository scanEvents,
    IDeviceRepository devices,
    TimeProvider clock,
    ILogger<RecordScan> logger)
{
    /// <summary>Fields the server sets or Cosmos reserves: clients can't send them as extra fields.</summary>
    public static readonly IReadOnlySet<string> ReservedFields =
        new HashSet<string>(["id", "receivedAt", "scanId", "barcode", "deviceId", "role", "scannedAt", "clientSha"]);

    private const int MaxClientShaLength = 64;

    public async Task<RecordScanResult> ExecuteAsync(RecordScanCommand command, CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        if (!Guid.TryParseExact(command.ScanId, "D", out var scanId))
        {
            errors.Add("scanId must be a UUID (e.g. 0b1c3f1e-6d0e-4c0a-9e0a-3a4d7c1f9a21).");
        }

        if (!Domain.Barcode.TryCreate(command.Barcode, out var barcode, out var barcodeError))
        {
            errors.Add(barcodeError!);
        }

        if (!DeviceIds.IsValid(command.DeviceId))
        {
            errors.Add("deviceId is required: lowercase letters, digits and dashes.");
        }

        if (!RoleNames.TryParse(command.Role, out var role))
        {
            errors.Add("role must be 'in' or 'out'.");
        }

        if (!TryParseTimestamp(command.ScannedAt, out var scannedAt))
        {
            errors.Add("scannedAt must be an ISO 8601 timestamp with a timezone (e.g. 2026-10-16T18:42:07.123Z).");
        }

        if (string.IsNullOrWhiteSpace(command.ClientSha) || command.ClientSha.Length > MaxClientShaLength)
        {
            errors.Add($"clientSha is required (at most {MaxClientShaLength} characters).");
        }

        foreach (var name in command.ExtraFields.Keys)
        {
            if (ReservedFields.Contains(name) || name.StartsWith('_'))
            {
                errors.Add($"'{name}' is a reserved field name.");
            }
        }

        if (errors.Count > 0)
        {
            return new RecordScanResult.Invalid(errors);
        }

        var receivedAt = clock.GetUtcNow();
        var scanEvent = new ScanEvent(
            scanId, barcode!, command.DeviceId!, role, scannedAt.ToUniversalTime(), receivedAt, command.ClientSha!);

        var created = await scanEvents.CreateIfNotExistsAsync(scanEvent, command.ExtraFields, cancellationToken);
        if (!created)
        {
            LogResend(scanId);
        }

        await UpdateDeviceAsync(scanEvent, cancellationToken);
        return new RecordScanResult.Recorded(scanId, WasAlreadyRecorded: !created);
    }

    private async Task UpdateDeviceAsync(ScanEvent scanEvent, CancellationToken cancellationToken)
    {
        var existing = await devices.GetAsync(scanEvent.DeviceId, cancellationToken);
        if (existing is not null && existing.Role != scanEvent.Role)
        {
            LogRoleChanged(scanEvent.DeviceId, existing.Role.ToWireName(), scanEvent.Role.ToWireName());
        }

        // A resent or queued scan can be older than the latest one: never move LastScanAt backwards.
        var lastScanAt = existing?.LastScanAt is { } previous && previous > scanEvent.ScannedAt
            ? previous
            : scanEvent.ScannedAt;
        await devices.UpsertAsync(
            new Device(scanEvent.DeviceId, scanEvent.Role, scanEvent.ReceivedAt, lastScanAt), cancellationToken);
    }

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        // Require an explicit offset or Z: a bare local time would be ambiguous.
        return !string.IsNullOrEmpty(value)
            && (value.EndsWith('Z') || value.EndsWith('z') || HasOffset(value))
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }

    private static bool HasOffset(string value) =>
        value.Length > 6 && (value[^6] is '+' or '-') && value[^3] == ':';

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan {ScanId} was already recorded (resend); storing nothing new.")]
    private partial void LogResend(Guid scanId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device {DeviceId} changed role from {OldRole} to {NewRole}.")]
    private partial void LogRoleChanged(string deviceId, string oldRole, string newRole);
}
