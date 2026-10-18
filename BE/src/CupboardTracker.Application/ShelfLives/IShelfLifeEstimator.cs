using CupboardTracker.Domain.Products;

namespace CupboardTracker.Application.ShelfLives;

/// <summary>What the estimator is told about a product: whatever we have (#52, spike #40).</summary>
public sealed record ProductDescription(string Name, string? Brand, string? Size, IReadOnlyList<string> CategoryTags);

/// <summary>A shelf-life estimate: days, the usual storage it's for, and how sure it is.</summary>
public sealed record ShelfLifeEstimate(int Days, Storage UsualStorage, double Confidence, bool LowConfidence);

public abstract record ShelfLifeResult
{
    public sealed record Estimated(ShelfLifeEstimate Estimate) : ShelfLifeResult;

    /// <summary>Couldn't be reached after retries (429/529, 5xx, timeout, network): try again later.</summary>
    public sealed record Unavailable(string Reason) : ShelfLifeResult;

    /// <summary>A request the estimator refused (400/401/413/415/422, or an answer we can't read): a bug or bad config.</summary>
    public sealed record Failed(string Reason) : ShelfLifeResult;

    /// <summary>No key yet (the Key Vault secret isn't set, or its reference hasn't resolved).</summary>
    public sealed record NotConfigured : ShelfLifeResult;
}

/// <summary>Estimates how long a product usually keeps. Implemented with Jev in Infrastructure.</summary>
public interface IShelfLifeEstimator
{
    Task<ShelfLifeResult> EstimateAsync(ProductDescription product, CancellationToken cancellationToken);
}
