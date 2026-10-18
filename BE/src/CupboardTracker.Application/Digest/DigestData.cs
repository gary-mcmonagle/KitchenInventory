namespace CupboardTracker.Application.Digest;

/// <summary>Everything the digest shows, already grouped and sorted. Built by <see cref="SendDailyDigest"/>, rendered by
/// <see cref="BuildDailyDigest"/>.</summary>
public sealed record DigestData(
    DateOnly Date,
    IReadOnlyList<DigestLine> CheckedIn,
    IReadOnlyList<DigestLine> CheckedOut,
    IReadOnlyList<DigestLine> InStock,
    IReadOnlyList<DigestLine> UnknownInStock,
    IReadOnlyList<DigestUseFirstLine>? UseFirst = null)
{
    /// <summary>Products to use first (#52), soonest first. Empty: the section is left out.</summary>
    public IReadOnlyList<DigestUseFirstLine> UseFirstLines => UseFirst ?? [];

    public int CheckedInCount => CheckedIn.Sum(l => l.Count);

    public int CheckedOutCount => CheckedOut.Sum(l => l.Count);

    public bool IsQuiet => CheckedIn.Count == 0 && CheckedOut.Count == 0;
}

/// <summary>One product (or unknown barcode) and how many units.</summary>
public sealed record DigestLine(string Name, int Count);

/// <summary>A product to use first: how many units, and the badge text for its soonest one ("Est. tomorrow").</summary>
public sealed record DigestUseFirstLine(string Name, int Count, string Badge, string? StateNote = null);
