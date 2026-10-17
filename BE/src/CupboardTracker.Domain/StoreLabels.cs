namespace CupboardTracker.Domain;

/// <summary>
/// In-store printed labels (deli, weighed items): EAN-13 codes starting 20–29 only. EAN-8 and UPC-A codes starting
/// with 2 are normal products (e.g. Lidl's 20590093), per the Open Food Facts spike.
/// </summary>
public static class StoreLabels
{
    public static bool IsStoreLabel(Barcode barcode) => barcode.Value.Length == 13 && barcode.Value[0] == '2';
}
