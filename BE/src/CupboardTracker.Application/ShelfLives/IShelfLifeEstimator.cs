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

/// <summary>How long a product keeps in one state, and where to keep it then (#65).</summary>
public sealed record StateEstimate(int Days, Storage Storage, double Confidence, bool LowConfidence);

/// <summary>Opened, frozen and defrosted estimates for a product, from one request (#65).</summary>
public sealed record StateEstimates(StateEstimate Opened, StateEstimate Frozen, StateEstimate Defrosted);

public abstract record StateEstimatesResult
{
    public sealed record Estimated(StateEstimates Estimates) : StateEstimatesResult;

    /// <summary>As <see cref="ShelfLifeResult.Unavailable"/>: try again later.</summary>
    public sealed record Unavailable(string Reason) : StateEstimatesResult;

    /// <summary>As <see cref="ShelfLifeResult.Failed"/>: a bug or bad config.</summary>
    public sealed record Failed(string Reason) : StateEstimatesResult;

    public sealed record NotConfigured : StateEstimatesResult;
}

/// <summary>Jev's aisle and icon for a product (#71, #72): each only when it was asked, with Jev's confidence.</summary>
public sealed record Classification(string? AisleId, double AisleConfidence, string? Icon, double IconConfidence);

public abstract record ClassificationResult
{
    public sealed record Classified(Classification Classification) : ClassificationResult;

    public sealed record Unavailable(string Reason) : ClassificationResult;

    public sealed record Failed(string Reason) : ClassificationResult;

    public sealed record NotConfigured : ClassificationResult;
}

/// <summary>Jev's answer to "meat, fish or neither?" (#112), with its confidence.</summary>
public abstract record AnimalContentResult
{
    public sealed record Classified(AnimalContent Value, double Confidence) : AnimalContentResult;

    public sealed record Unavailable(string Reason) : AnimalContentResult;

    public sealed record Failed(string Reason) : AnimalContentResult;

    public sealed record NotConfigured : AnimalContentResult;
}

/// <summary>Estimates how long a product usually keeps. Implemented with Jev in Infrastructure.</summary>
public interface IShelfLifeEstimator
{
    Task<ShelfLifeResult> EstimateAsync(ProductDescription product, CancellationToken cancellationToken);

    /// <summary>How long it keeps once opened, in the freezer and once defrosted, and where to keep it (#65).
    /// <paramref name="usualStorage"/> is where it's kept sealed (from its shelf life).</summary>
    Task<StateEstimatesResult> EstimateStatesAsync(ProductDescription product, Storage usualStorage, CancellationToken cancellationToken);

    /// <summary>The product's aisle (one of <see cref="Aisles.All"/>) and/or icon (one of <see cref="ProductIcons.Names"/>),
    /// in one request (#71, #72). <paramref name="usualStorage"/> when known.</summary>
    Task<ClassificationResult> ClassifyAsync(
        ProductDescription product, Storage? usualStorage, bool aisle, bool icon, CancellationToken cancellationToken);

    /// <summary>Whether it's meat, fish or neither (#112). <paramref name="meatOrFish"/>: it's in the Meat &amp; fish
    /// aisle, so the only question is which.</summary>
    Task<AnimalContentResult> ClassifyAnimalContentAsync(ProductDescription product, bool meatOrFish, CancellationToken cancellationToken);
}
