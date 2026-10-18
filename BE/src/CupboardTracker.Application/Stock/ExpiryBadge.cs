using System.Globalization;
using CupboardTracker.Domain.Stock;

namespace CupboardTracker.Application.Stock;

/// <summary>
/// The "Est. …" text for an estimated expiry date, as on the stock list (#52; the UI's <c>expiryBadge</c> follows the
/// same rules): <c>Est. expired</c>, <c>Est. today</c>, <c>Est. tomorrow</c>, <c>Est. n days</c> up to 14 days, then
/// <c>Est. 2 Nov</c> up to 6 months away, then <c>Est. Oct 2027</c> (a day and month further out reads like this year).
/// </summary>
public static class ExpiryBadge
{
    public const int DaysShownAsCount = 14;
    public const int DaysShownAsDayAndMonth = 182;

    private static readonly CultureInfo UkCulture = CultureInfo.GetCultureInfo("en-GB");

    public static string Text(DateOnly expiry, DateOnly today)
    {
        var days = EstimatedExpiry.DaysUntil(expiry, today);
        return "Est. " + days switch
        {
            < 0 => "expired",
            0 => "today",
            1 => "tomorrow",
            <= DaysShownAsCount => $"{days} days",
            > DaysShownAsDayAndMonth => expiry.ToString("MMM yyyy", UkCulture),
            _ => expiry.ToString("d MMM", UkCulture),
        };
    }
}
