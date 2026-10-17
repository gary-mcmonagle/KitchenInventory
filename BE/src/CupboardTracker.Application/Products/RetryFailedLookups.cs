using CupboardTracker.Domain.Products;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Products;

/// <summary>
/// Timer: retries lookupFailed products (oldest attempt first, a few per run, through the retry share of the rate
/// budget), so items scanned during a stock-take or an outage get identified without rescanning.
/// </summary>
public sealed partial class RetryFailedLookups(
    IProductRepository products, ProductResolver resolver, TimeProvider clock, ILogger<RetryFailedLookups> logger)
{
    public const int MaxPerRun = 10;
    public static readonly TimeSpan ErrorAfter = TimeSpan.FromHours(24);

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var failed = await products.GetLookupFailedAsync(MaxPerRun, cancellationToken);
        var resolved = 0;
        foreach (var product in failed)
        {
            var result = await resolver.RetryAsync(product, LookupBudget.Retry, cancellationToken);
            if (result.Status != ProductStatus.LookupFailed)
            {
                resolved++;
            }
            else if (result.LookupFailedSince is { } since && clock.GetUtcNow() - since > ErrorAfter)
            {
                LogStillFailing(result.ProductId, result.Barcodes[0], since);
            }
        }

        LogRun(failed.Count, resolved);
        return resolved;
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Product {ProductId} ({Barcode}) has failed lookup since {Since}: still lookupFailed after 24 hours.")]
    private partial void LogStillFailing(Guid productId, string barcode, DateTimeOffset since);

    [LoggerMessage(Level = LogLevel.Information, Message = "Lookup retry: {Tried} tried, {Resolved} resolved.")]
    private partial void LogRun(int tried, int resolved);
}
