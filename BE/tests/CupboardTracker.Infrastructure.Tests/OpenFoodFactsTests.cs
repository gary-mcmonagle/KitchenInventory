using System.Net;
using CupboardTracker.Application.Products;
using CupboardTracker.Infrastructure.OpenFoodFacts;
using Microsoft.Extensions.Time.Testing;

namespace CupboardTracker.Infrastructure.Tests;

public sealed class OpenFoodFactsTests
{
    private static readonly OpenFoodFactsOptions Options =
        new(new Uri("https://off.example.test"), "contact@example.test", 10) { FirstRetryDelay = TimeSpan.Zero };

    private sealed class Handler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int next;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var respond = responses[Math.Min(next++, responses.Length - 1)];
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static readonly Func<HttpRequestMessage, HttpResponseMessage> Bisto = _ => Json(HttpStatusCode.OK, """
        {"code":"5000354922831","product":{"product_name":"Beef Gravy","brands":"Bisto, Premier","quantity":" 230g ",
         "image_front_small_url":"https://img/small.jpg","image_front_url":"https://img/full.jpg",
         "categories_tags":["en:groceries","en:gravy"]},"result":{"id":"product_found"},"status":"success"}
        """);

    private static (OpenFoodFactsProductLookup Lookup, Handler Handler) Make(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new Handler(responses);
        var clock = TimeProvider.System;
        return (new OpenFoodFactsProductLookup(new HttpClient(handler), Options, new OpenFoodFactsThrottles(Options, clock), clock), handler);
    }

    [Fact]
    public async Task Maps_the_fields_and_sends_the_user_agent_and_barcode_as_scanned()
    {
        var (lookup, handler) = Make(Bisto);
        var result = await lookup.LookupAsync("049733830119", LookupBudget.CheckIn, CancellationToken.None);

        var found = Assert.IsType<ProductLookupResult.Found>(result);
        Assert.Equal("Beef Gravy", found.Details.DisplayName);
        Assert.Equal("Bisto", found.Details.Brand);            // first of brands
        Assert.Equal("230g", found.Details.Size);              // quantity, trimmed
        Assert.Equal("https://img/small.jpg", found.Details.ImageUrl);
        Assert.Equal(["en:groceries", "en:gravy"], found.Details.CategoryTags);
        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("/api/v3/product/049733830119?", request.RequestUri!.PathAndQuery, StringComparison.Ordinal); // 12 digits, not padded
        Assert.Equal("CupboardTracker/1.0 (contact@example.test)", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public void Prefers_the_english_name_trimmed_but_otherwise_exactly_as_returned()
    {
        var result = OpenFoodFactsProductLookup.Parse("""
            {"product":{"product_name":"Mittelscharfer Senf","product_name_en":"  SEMI-SKIMMED BRITISH MILK  "}}
            """);
        Assert.Equal("SEMI-SKIMMED BRITISH MILK", Assert.IsType<ProductLookupResult.Found>(result).Details.DisplayName); // not title-cased
        var bulgarian = OpenFoodFactsProductLookup.Parse("""{"product":{"product_name":"горчица"}}""");
        Assert.Equal("горчица", Assert.IsType<ProductLookupResult.Found>(bulgarian).Details.DisplayName);
    }

    [Fact]
    public void An_empty_name_is_not_found() =>
        Assert.IsType<ProductLookupResult.NotFound>(OpenFoodFactsProductLookup.Parse("""{"product":{"product_name":"  "}}"""));

    [Fact]
    public async Task A_404_is_not_found()
    {
        var (lookup, _) = Make(_ => Json(HttpStatusCode.NotFound, """{"result":{"id":"product_not_found"},"status":"failure"}"""));
        Assert.IsType<ProductLookupResult.NotFound>(await lookup.LookupAsync("5098765432105", LookupBudget.CheckIn, CancellationToken.None));
    }

    public static TheoryData<int, string> TransientResponses => new()
    {
        { 429, "<html><head><title>429 Too Many Requests</title></head></html>" }, // OFF's nginx page, no Retry-After
        { 503, "Service Unavailable" },
        { 200, "<html>not json</html>" },
    };

    [Theory]
    [MemberData(nameof(TransientResponses))]
    public async Task Rate_limits_server_errors_and_non_json_are_transient_after_retries(int status, string body)
    {
        var (lookup, handler) = Make(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        var result = await lookup.LookupAsync("5000354922831", LookupBudget.CheckIn, CancellationToken.None);
        Assert.IsType<ProductLookupResult.Transient>(result);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_transient_failure_followed_by_success_is_found()
    {
        var (lookup, handler) = Make(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), Bisto);
        Assert.IsType<ProductLookupResult.Found>(await lookup.LookupAsync("5000354922831", LookupBudget.CheckIn, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_network_error_is_transient()
    {
        var (lookup, _) = Make(_ => throw new HttpRequestException("boom", null, null));
        Assert.IsType<ProductLookupResult.Transient>(await lookup.LookupAsync("5000354922831", LookupBudget.CheckIn, CancellationToken.None));
    }

    [Fact]
    public void The_budget_shares_add_up_to_the_configured_maximum()
    {
        Assert.Equal(8, Options.CheckInShare);
        Assert.Equal(2, Options.RetryShare);
        var five = Options with { MaxRequestsPerMinute = 5 };
        Assert.Equal(5, five.CheckInShare + five.RetryShare);
    }

    [Fact]
    public async Task The_throttle_never_exceeds_its_limit_and_queued_callers_complete()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        using var throttle = new SlidingWindowThrottle(2, clock);

        await throttle.WaitAsync(CancellationToken.None);
        await throttle.WaitAsync(CancellationToken.None);
        var third = throttle.WaitAsync(CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(third.IsCompleted); // two in this minute already: the third waits

        clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(third.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await third.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // queued, then completes
    }
}
