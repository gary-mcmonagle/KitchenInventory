namespace CupboardTracker.Application.Digest;

/// <summary>Everything the digest shows, already grouped and sorted. Built by <see cref="SendDailyDigest"/>, rendered by
/// <see cref="BuildDailyDigest"/>.</summary>
public sealed record DigestData(
    DateOnly Date,
    IReadOnlyList<DigestLine> CheckedIn,
    IReadOnlyList<DigestLine> CheckedOut,
    IReadOnlyList<DigestLine> InStock,
    IReadOnlyList<DigestLine> UnknownInStock)
{
    public int CheckedInCount => CheckedIn.Sum(l => l.Count);

    public int CheckedOutCount => CheckedOut.Sum(l => l.Count);

    public bool IsQuiet => CheckedIn.Count == 0 && CheckedOut.Count == 0;
}

/// <summary>One product (or unknown barcode) and how many units.</summary>
public sealed record DigestLine(string Name, int Count);
