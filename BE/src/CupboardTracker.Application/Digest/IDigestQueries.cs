namespace CupboardTracker.Application.Digest;

/// <summary>A unit as the digest needs it: which product, and the barcode it was scanned with (if any).</summary>
/// <summary><see cref="StateNote"/>: "opened 12 Oct" or "defrosted 12 Oct" when that's what its estimate is from (#65).</summary>
public sealed record DigestUnit(Guid ProductId, string? Barcode, DateOnly? EstimatedExpiryDate = null, string? StateNote = null);

/// <summary>The digest's reads (cross-partition over inventoryItems; tiny at household volume).</summary>
public interface IDigestQueries
{
    Task<IReadOnlyList<DigestUnit>> GetInStockAsync(CancellationToken cancellationToken);

    /// <summary>Units checked in during the window (start inclusive, end exclusive).</summary>
    Task<IReadOnlyList<DigestUnit>> GetCheckedInAsync(DigestWindow window, CancellationToken cancellationToken);

    /// <summary>Units checked out during the window (start inclusive, end exclusive).</summary>
    Task<IReadOnlyList<DigestUnit>> GetCheckedOutAsync(DigestWindow window, CancellationToken cancellationToken);
}

/// <summary>One record per digest day, created before sending so a second run that day sends nothing.</summary>
public interface ISentDigestRepository
{
    /// <summary>False if that day's digest is already claimed (sent, or being sent).</summary>
    Task<bool> TryClaimAsync(DigestWindow window, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Removes the claim after a failed send, so the retry can send.</summary>
    Task ReleaseAsync(DateOnly day, CancellationToken cancellationToken);
}
