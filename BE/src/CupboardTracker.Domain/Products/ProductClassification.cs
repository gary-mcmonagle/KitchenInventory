namespace CupboardTracker.Domain.Products;

/// <summary>A product's aisle (#71): an <see cref="Aisles"/> ID, from Jev or chosen by hand (<see cref="IsManual"/>).</summary>
public sealed record ProductAisle(string Id, string Source, double Confidence, DateTimeOffset At)
{
    public bool IsManual => Source == ShelfLife.ManualSource;
}

/// <summary>
/// A product's icon (#72): a <see cref="ProductIcons"/> name, or <c>null</c> when Jev wasn't sure enough
/// (below <see cref="ProductIcons.MinConfidence"/>), so it shows its aisle's icon. Chosen by hand: <see cref="IsManual"/>.
/// </summary>
public sealed record ProductIcon(string? Name, string Source, double Confidence, DateTimeOffset At)
{
    public bool IsManual => Source == ShelfLife.ManualSource;
}
