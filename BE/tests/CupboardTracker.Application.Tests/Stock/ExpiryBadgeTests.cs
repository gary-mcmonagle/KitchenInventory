using CupboardTracker.Application.Stock;

namespace CupboardTracker.Application.Tests.Stock;

public sealed class ExpiryBadgeTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);

    [Theory]
    [InlineData(-1, "Est. expired")]
    [InlineData(-30, "Est. expired")]
    [InlineData(0, "Est. today")]
    [InlineData(1, "Est. tomorrow")]
    [InlineData(3, "Est. 3 days")]
    [InlineData(4, "Est. 4 days")]
    [InlineData(14, "Est. 14 days")]
    [InlineData(15, "Est. 2 Nov")]
    [InlineData(182, "Est. 18 Apr")]
    [InlineData(183, "Est. Apr 2027")]
    [InlineData(364, "Est. Oct 2027")] // never "Est. 17 Oct", which reads like today
    [InlineData(730, "Est. Oct 2028")]
    public void Badge_text(int days, string expected) =>
        Assert.Equal(expected, ExpiryBadge.Text(Today.AddDays(days), Today));
}
