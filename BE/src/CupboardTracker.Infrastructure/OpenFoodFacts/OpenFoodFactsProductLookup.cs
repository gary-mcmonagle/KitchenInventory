using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Products;
using CupboardTracker.Domain.Products;

namespace CupboardTracker.Infrastructure.OpenFoodFacts;

/// <summary>
/// Open Food Facts v3 product lookup (docs/spikes/28-open-food-facts.md). 404 or no usable name → not found;
/// 429 (nginx HTML, no Retry-After), 5xx, timeouts, network errors and non-JSON bodies → transient, retried with back-off.
/// Barcodes are sent exactly as scanned; OFF's zero-padded <c>code</c> is ignored.
/// </summary>
public sealed class OpenFoodFactsProductLookup(
    HttpClient http, OpenFoodFactsOptions options, OpenFoodFactsThrottles throttles, TimeProvider clock) : IProductLookup
{
    private const string Fields = "product_name,product_name_en,brands,quantity,image_front_url,image_front_small_url,categories_tags";

    public async Task<ProductLookupResult> LookupAsync(string barcode, LookupBudget budget, CancellationToken cancellationToken)
    {
        var throttle = budget == LookupBudget.Retry ? throttles.Retry : throttles.CheckIn;
        var delay = options.FirstRetryDelay;
        var reason = "no attempts";
        for (var attempt = 1; attempt <= options.AttemptsPerLookup; attempt++)
        {
            await throttle.WaitAsync(cancellationToken);
            var result = await TryOnceAsync(barcode, cancellationToken);
            if (result is not ProductLookupResult.Transient transient)
            {
                return result;
            }

            reason = transient.Reason;
            if (attempt < options.AttemptsPerLookup)
            {
                await Task.Delay(delay, clock, cancellationToken);
                delay *= 2;
            }
        }

        return new ProductLookupResult.Transient($"{reason} (after {options.AttemptsPerLookup} attempts)");
    }

    private async Task<ProductLookupResult> TryOnceAsync(string barcode, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, new Uri(options.BaseUrl, $"/api/v3/product/{Uri.EscapeDataString(barcode)}?fields={Fields}"));
            request.Headers.UserAgent.ParseAdd(options.UserAgent);
            using var response = await http.SendAsync(request, timeout.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ProductLookupResult.NotFound();
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                return new ProductLookupResult.Transient($"HTTP {(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ProductLookupResult.Transient($"unexpected HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return Parse(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProductLookupResult.Transient("timeout");
        }
        catch (HttpRequestException e)
        {
            return new ProductLookupResult.Transient($"network error ({e.HttpRequestError})");
        }
    }

    internal static ProductLookupResult Parse(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return new ProductLookupResult.Transient("non-JSON response");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object)
            {
                return new ProductLookupResult.NotFound();
            }

            // Display name: product_name_en, else product_name, trimmed, otherwise exactly as returned (Gary, 2026-10-17).
            var name = Text(product, "product_name_en") ?? Text(product, "product_name");
            if (name is null)
            {
                return new ProductLookupResult.NotFound();
            }

            var brand = Text(product, "brands")?.Split(',')[0].Trim() is { Length: > 0 } b ? b : null;
            var tags = product.TryGetProperty("categories_tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
            return new ProductLookupResult.Found(new ProductDetails(
                name, brand, Text(product, "quantity"),
                Text(product, "image_front_small_url") ?? Text(product, "image_front_url"), tags));
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString()!.Trim() is { Length: > 0 } text
            ? text
            : null;
}

/// <summary>The two shares of the Open Food Facts rate budget (singleton per app instance).</summary>
public sealed class OpenFoodFactsThrottles(OpenFoodFactsOptions options, TimeProvider clock) : IDisposable
{
    public SlidingWindowThrottle CheckIn { get; } = new(options.CheckInShare, clock);

    public SlidingWindowThrottle Retry { get; } = new(options.RetryShare, clock);

    public void Dispose()
    {
        CheckIn.Dispose();
        Retry.Dispose();
    }
}
