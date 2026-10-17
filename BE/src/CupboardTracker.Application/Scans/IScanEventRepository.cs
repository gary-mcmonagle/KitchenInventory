using System.Text.Json;
using CupboardTracker.Domain;

namespace CupboardTracker.Application.Scans;

public interface IScanEventRepository
{
    /// <summary>
    /// Stores the scan event (append-only, id = scan ID) with any extra client fields as-is.
    /// Returns false when a scan with that ID is already stored (a resend), true when it was created.
    /// </summary>
    Task<bool> CreateIfNotExistsAsync(
        ScanEvent scanEvent, IReadOnlyDictionary<string, JsonElement> extraFields, CancellationToken cancellationToken);
}
