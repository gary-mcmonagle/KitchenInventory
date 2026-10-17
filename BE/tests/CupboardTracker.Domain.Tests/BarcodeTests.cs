using CupboardTracker.Domain;

namespace CupboardTracker.Domain.Tests;

public sealed class BarcodeTests
{
    // Real barcodes from the Open Food Facts spike sample (#28): EAN-13, EAN-8, UPC-A.
    [Theory]
    [InlineData("5000354922831")]
    [InlineData("4056489899112")]
    [InlineData("20590093")]
    [InlineData("049733830119")]
    [InlineData("615357122468")]
    public void Real_barcodes_are_valid(string value)
    {
        Assert.True(Barcode.TryCreate(value, out var barcode, out var error));
        Assert.Equal(value, barcode!.Value);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("5000354922832", "check digit")]
    [InlineData("20590094", "check digit")]
    [InlineData("049733830110", "check digit")]
    [InlineData("123", "8, 12 or 13")]
    [InlineData("50003549228311", "8, 12 or 13")]
    [InlineData("50003549228a1", "digits only")]
    [InlineData("５000354922831", "digits only")] // full-width 5
    [InlineData("", "required")]
    [InlineData(null, "required")]
    public void Invalid_barcodes_are_rejected_with_a_reason(string? value, string reason)
    {
        Assert.False(Barcode.TryCreate(value, out var barcode, out var error));
        Assert.Null(barcode);
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scanner-in", true)]
    [InlineData("cupboard-pi", true)]
    [InlineData("Scanner-In", false)]
    [InlineData("-scanner", false)]
    [InlineData("a", false)]
    [InlineData(null, false)]
    public void Device_ids_follow_the_pi_rule(string? value, bool valid) => Assert.Equal(valid, DeviceIds.IsValid(value));
}
