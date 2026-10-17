using CupboardTracker.Application.Digest;
using Microsoft.Azure.Functions.Worker;

namespace CupboardTracker.Functions.Api.Digest;

/// <summary>
/// The 9pm digest (#46). Linux Flex Consumption has no time zone setting, so this runs at 20:00 and 21:00 UTC and
/// only the run in the 21:00 hour in the UK sends (spike #39). A failed send is retried here; each failure logs an Error.
/// </summary>
public sealed class DailyDigestTimer(SendDailyDigest send)
{
    [Function("dailyDigest")]
    [ExponentialBackoffRetry(4, "00:00:30", "00:10:00")]
    public Task Run([TimerTrigger("0 0 20,21 * * *")] TimerInfo timer, CancellationToken cancellationToken) =>
        send.ExecuteAsync(cancellationToken);
}
