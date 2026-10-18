namespace CupboardTracker.Domain;

/// <summary>
/// In-store printed labels (deli, weighed items): EAN-13 codes starting 20–29 only, GS1's Restricted Circulation
/// Numbers "within a geographic region" (GSCN 23-006). EAN-8 and UPC-A codes starting with 2 are normal products
/// (e.g. Lidl's 20590093), per the Open Food Facts spike. A store-label check-out can't be matched to a unit (#58).
/// </summary>
public static class StoreLabels
{
    public static bool IsStoreLabel(Barcode barcode) => IsStoreLabel(barcode.Value);

    public static bool IsStoreLabel(string barcode) => barcode.Length == 13 && barcode[0] == '2';

    /// <summary>The note on a store-label check-out (it can't be matched to a unit).</summary>
    public const string CheckOutNote = "Store label: check it out from the app";
}
