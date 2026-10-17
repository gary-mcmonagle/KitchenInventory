using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Stock;

public interface IUnitRepository
{
    /// <summary>Creates the unit (id = check-in scan ID). False if it already exists (a replay).</summary>
    Task<bool> CreateIfNotExistsAsync(Unit unit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Versioned<Unit>>> GetInStockAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Replaces the unit only if it hasn't changed since it was read (ETag). False on a conflict.</summary>
    Task<bool> TryReplaceAsync(Versioned<Unit> current, Unit updated, CancellationToken cancellationToken);

    /// <summary>A unit by its id, whatever its product (cross-partition; for undo).</summary>
    Task<Versioned<Unit>?> FindAsync(Guid unitId, CancellationToken cancellationToken);

    /// <summary>Whether a unit of this product was already checked out by this scan (a replay).</summary>
    Task<bool> IsCheckedOutByScanAsync(Guid productId, Guid scanId, CancellationToken cancellationToken);
}

public interface IUnmatchedCheckOutRepository
{
    Task<bool> ExistsAsync(Guid scanId, CancellationToken cancellationToken);

    /// <summary>Records it (id = scan ID). False if it already exists (a replay).</summary>
    Task<bool> CreateIfNotExistsAsync(UnmatchedCheckOut unmatched, CancellationToken cancellationToken);
}
