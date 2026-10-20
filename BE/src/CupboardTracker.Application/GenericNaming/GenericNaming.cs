using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.GenericNaming;

/// <summary>The generic name records (#90): stored as <c>genericName</c> documents in the products container.</summary>
public interface IGenericNameRepository
{
    Task<IReadOnlyList<GenericName>> ListAsync(CancellationToken cancellationToken);

    Task<GenericName?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>Creates the record unless one with that id exists, and returns the one that's stored either way (so
    /// two products never create two records with the same id).</summary>
    Task<GenericName> GetOrCreateAsync(GenericName name, CancellationToken cancellationToken);

    /// <summary>Turns "add when we run out" (#89) on or off. Null if there's no such record.</summary>
    Task<GenericName?> SetAddWhenOutAsync(string id, bool enabled, Guid by, DateTimeOffset at, CancellationToken cancellationToken);
}

public abstract record GenericMatchResult
{
    public sealed record Matched(GenericName Name, double Confidence) : GenericMatchResult;

    /// <summary>"None of these", or a pick below the confidence threshold.</summary>
    public sealed record NoMatch : GenericMatchResult;

    public sealed record Unavailable(string Reason) : GenericMatchResult;

    public sealed record Failed(string Reason) : GenericMatchResult;

    public sealed record NotConfigured : GenericMatchResult;
}

/// <summary>Picks which of some generic names is the same kind of product, or none (Jev <c>choice</c> in
/// Infrastructure). Callers keep the options within Jev's limit (<see cref="GenericNameSettings.BatchSize"/>).</summary>
public interface IGenericNameMatcher
{
    Task<GenericMatchResult> MatchAsync(ProductDescription product, IReadOnlyList<GenericName> options, CancellationToken cancellationToken);
}

public abstract record GenericWriteResult
{
    public sealed record Written(string Name) : GenericWriteResult;

    public sealed record Unavailable(string Reason) : GenericWriteResult;

    public sealed record Failed(string Reason) : GenericWriteResult;

    public sealed record NotConfigured : GenericWriteResult;
}

/// <summary>Writes a new level A generic name (spike #82) when no existing one fits (Foundry gpt-4o in Infrastructure,
/// throttled so recipes and photo identification keep their share of the deployment).</summary>
public interface IGenericNameWriter
{
    Task<GenericWriteResult> WriteAsync(
        ProductDescription product, string aisleName, IReadOnlyList<string> examples, CancellationToken cancellationToken);
}

/// <summary>From app settings (#90): GENERIC_NAME_MATCH_MIN_CONFIDENCE (default 0.9, calibrated on 77 real products:
/// below it Jev's wrong merges outnumber its right ones) and GENERIC_NAME_BATCH_SIZE (default 254: Jev's 255 options
/// less "None of these").</summary>
public sealed record GenericNameSettings(double MatchMinConfidence = GenericNameSettings.DefaultMatchMinConfidence, int BatchSize = GenericNameSettings.DefaultBatchSize)
{
    public const double DefaultMatchMinConfidence = 0.9;
    public const int DefaultBatchSize = 254;
}
