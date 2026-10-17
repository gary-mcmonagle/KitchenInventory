using CupboardTracker.Application.Products;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Tests;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IEnumerable<string> At(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

internal sealed class FakeLookup : IProductLookup
{
    public Queue<ProductLookupResult> Results { get; } = new();

    public List<(string Barcode, LookupBudget Budget)> Calls { get; } = [];

    public ProductLookupResult Default { get; set; } = new ProductLookupResult.NotFound();

    public Task<ProductLookupResult> LookupAsync(string barcode, LookupBudget budget, CancellationToken cancellationToken)
    {
        Calls.Add((barcode, budget));
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Default);
    }
}

internal sealed class FakeProducts : IProductRepository
{
    public Dictionary<string, Guid> Lookups { get; } = [];

    public Dictionary<Guid, Product> Products { get; } = [];

    /// <summary>Simulates another writer winning a race: runs just before a claim or create is applied.</summary>
    public Action? BeforeWrite { get; set; }

    public Task<Guid?> GetProductIdForBarcodeAsync(string barcode, CancellationToken cancellationToken) =>
        Task.FromResult(Lookups.TryGetValue(barcode, out var id) ? id : (Guid?)null);

    public Task<Guid> ClaimBarcodeAsync(string barcode, Guid proposedProductId, CancellationToken cancellationToken)
    {
        BeforeWrite?.Invoke();
        if (!Lookups.TryGetValue(barcode, out var owner))
        {
            Lookups[barcode] = owner = proposedProductId;
        }

        return Task.FromResult(owner);
    }

    public Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult(Products.GetValueOrDefault(productId));

    public Task<Product> CreateIfNotExistsAsync(Product product, CancellationToken cancellationToken)
    {
        BeforeWrite?.Invoke();
        if (!Products.TryGetValue(product.ProductId, out var stored))
        {
            Products[product.ProductId] = stored = product;
        }

        return Task.FromResult(stored);
    }

    public Task<bool> TryReplaceLookupFailedAsync(Product updated, CancellationToken cancellationToken)
    {
        if (Products.TryGetValue(updated.ProductId, out var current) && current.Status == ProductStatus.LookupFailed)
        {
            Products[updated.ProductId] = updated;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> TryReplaceUnnamedAsync(Product updated, CancellationToken cancellationToken)
    {
        BeforeWrite?.Invoke();
        if (!Products.TryGetValue(updated.ProductId, out var current) || !current.IsUnnamed)
        {
            return Task.FromResult(false);
        }

        Products[updated.ProductId] = updated;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<Product>> GetLookupFailedAsync(int max, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Product>>(Products.Values
            .Where(p => p.Status == ProductStatus.LookupFailed).OrderBy(p => p.LastLookupAttemptAt).Take(max).ToList());
}

internal sealed class FakeUnits : IUnitRepository
{
    public Dictionary<Guid, (Unit Unit, int Version)> Units { get; } = [];

    /// <summary>Unit IDs a concurrent writer will check out just before our replace (an ETag conflict).</summary>
    public HashSet<Guid> StolenBeforeReplace { get; } = [];

    public IEnumerable<Unit> InStock(Guid productId) =>
        Units.Values.Select(v => v.Unit).Where(u => u.ProductId == productId && u.Status == UnitStatus.InStock);

    public void Add(Unit unit) => Units[unit.UnitId] = (unit, 1);

    public Task<bool> CreateIfNotExistsAsync(Unit unit, CancellationToken cancellationToken)
    {
        if (Units.ContainsKey(unit.UnitId))
        {
            return Task.FromResult(false);
        }

        Add(unit);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<Versioned<Unit>>> GetInStockAsync(Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Versioned<Unit>>>(Units.Values
            .Where(v => v.Unit.ProductId == productId && v.Unit.Status == UnitStatus.InStock)
            .Select(v => new Versioned<Unit>(v.Unit, v.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList());

    public Task<bool> TryReplaceAsync(Versioned<Unit> current, Unit updated, CancellationToken cancellationToken)
    {
        if (StolenBeforeReplace.Remove(updated.UnitId))
        {
            var (unit, version) = Units[updated.UnitId];
            Units[updated.UnitId] = (unit.CheckOut(Guid.NewGuid(), unit.CheckedInAt, "scanner"), version + 1);
        }

        var stored = Units[updated.UnitId];
        if (stored.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) != current.ETag)
        {
            return Task.FromResult(false);
        }

        Units[updated.UnitId] = (updated, stored.Version + 1);
        return Task.FromResult(true);
    }

    public Task<Versioned<Unit>?> FindAsync(Guid unitId, CancellationToken cancellationToken) =>
        Task.FromResult(Units.TryGetValue(unitId, out var v)
            ? new Versioned<Unit>(v.Unit, v.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null);

    public Task<bool> IsCheckedOutByScanAsync(Guid productId, Guid scanId, CancellationToken cancellationToken) =>
        Task.FromResult(Units.Values.Any(v => v.Unit.ProductId == productId && v.Unit.CheckOutScanId == scanId));
}

internal sealed class FakeUnmatched : IUnmatchedCheckOutRepository
{
    public Dictionary<Guid, UnmatchedCheckOut> Records { get; } = [];

    public Task<bool> ExistsAsync(Guid scanId, CancellationToken cancellationToken) => Task.FromResult(Records.ContainsKey(scanId));

    public Task<bool> CreateIfNotExistsAsync(UnmatchedCheckOut unmatched, CancellationToken cancellationToken) =>
        Task.FromResult(Records.TryAdd(unmatched.ScanId, unmatched));
}

internal static class TestScans
{
    public static readonly DateTimeOffset At = new(2026, 10, 17, 14, 0, 0, TimeSpan.Zero);

    public static ScanEvent Make(string barcode, Role role, Guid? scanId = null, DateTimeOffset? at = null)
    {
        Assert.True(Barcode.TryCreate(barcode, out var b, out var error), error);
        return new ScanEvent(scanId ?? Guid.NewGuid(), b!, role == Role.In ? "scanner-in" : "scanner-out", role,
            at ?? At, at ?? At, "test");
    }
}
