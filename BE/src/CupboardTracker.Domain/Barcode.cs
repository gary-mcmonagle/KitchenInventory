namespace CupboardTracker.Domain;

/// <summary>
/// The EAN/UPC number printed on a pack, exactly as scanned: digits only, 8, 12 or 13 long
/// (EAN-8, UPC-A, EAN-13) with a valid GS1 check digit. Same rule as the Pi's <c>cupboard_pi.payloads</c>.
/// </summary>
public sealed record Barcode
{
    private Barcode(string value) => Value = value;

    public string Value { get; }

    public override string ToString() => Value;

    public static bool TryCreate(string? value, out Barcode? barcode, out string? error)
    {
        barcode = null;
        error = Validate(value);
        if (error is not null)
        {
            return false;
        }

        barcode = new Barcode(value!);
        return true;
    }

    private static string? Validate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "barcode is required.";
        }

        if (!value.All(char.IsAsciiDigit))
        {
            return "barcode must contain digits only.";
        }

        if (value.Length is not (8 or 12 or 13))
        {
            return "barcode must be 8, 12 or 13 digits.";
        }

        return HasValidCheckDigit(value) ? null : "barcode check digit is wrong.";
    }

    private static bool HasValidCheckDigit(string digits)
    {
        // From the right of the body (everything but the check digit), weights alternate 3, 1, 3, 1...
        var total = 0;
        for (int i = digits.Length - 2, position = 0; i >= 0; i--, position++)
        {
            total += (digits[i] - '0') * (position % 2 == 0 ? 3 : 1);
        }

        return (10 - total % 10) % 10 == digits[^1] - '0';
    }
}
