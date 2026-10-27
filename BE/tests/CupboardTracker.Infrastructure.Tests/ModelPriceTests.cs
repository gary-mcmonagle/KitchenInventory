using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CupboardTracker.Infrastructure.ModelTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CupboardTracker.Infrastructure.Tests;

/// <summary>Matching a model to its list price among Azure's Retail Prices rows (#121), on rows recorded from the real service.</summary>
public sealed class ModelPriceTests
{
    private static RetailRow Row(string meter, decimal price, string unit = "1M", string region = "uksouth") => new(meter, region, price, unit);

    private static readonly RetailRow[] Rows =
    [
        // gpt-6-luna (product GPT6) and the older 5.6 luna (product GPT5), both in UK South
        Row("6-luna ShortCo Inp Std Gl 1M Tokens", 0.0755m),
        Row("6-luna ShortCo Opt Std Gl 1M Tokens", 0.3774m),
        Row("6-luna ShortCo Cd Inp Std Gl 1M Tokens", 0.0075m),
        Row("6-luna ShortCo Cd Wr Std Gl 1M Tokens", 0.0943m),
        Row("6-luna ShortCo Inp Flex Gl 1M Tokens", 0.0377m),
        Row("6-luna ShortCo Opt Flex Gl 1M Tokens", 0.1887m),
        Row("6-luna LongCo Inp Std Gl 1M Tokens", 0.1509m),
        Row("6-luna LongCo Opt Std Gl 1M Tokens", 0.5661m),
        Row("5.6 luna ShortCo Inp Std Gl 1M Tokens", 0.1509m),
        Row("5.6 luna ShortCo Opt Std Gl 1M Tokens", 0.9057m),
        Row("5.6 luna ShortCo Inp PP Gl 1M Tokens", 0.3019m),
        // gpt-4o: several releases, regional and global, plus look-alikes
        Row("gpt 4o 1120 Inp regnl Tokens", 0.0023m, "1K"),
        Row("gpt 4o 1120 Outp regnl Tokens", 0.0091m, "1K"),
        Row("gpt 4o 1120 cached Inp regnl Tokens", 0.0011m, "1K"),
        Row("gpt 4o 1120 Batch Inp glbl Tokens", 0.0009m, "1K"),
        Row("gpt 4o 0513 Input global Tokens", 0.0038m, "1K"),
        Row("gpt 4o 0513 Output global Tokens", 0.0113m, "1K"),
        Row("gpt-4o-0806-Inp-glbl Tokens", 0.0019m, "1K"),
        Row("gpt-4o-mini-0718-Inp-glbl Tokens", 0.0001m, "1K"),
        Row("gpt-4o-mini-0718-Outp-glbl Tokens", 0.0005m, "1K"),
        Row("gpt-4o-rt-txt-1217 Outp glbl Tokens", 0.0151m, "1K"),
        // DeepSeek V4.1 Flash, global (listed under other regions)
        Row("V4.1 Flash Input Glbl 1M Tokens", 0.2264m, region: "eastasia"),
        Row("V4.1 Flash Output Glbl 1M Tokens", 0.9057m, region: "eastasia"),
        Row("V4.1 Flash cached Glbl 1M Tokens", 0.0045m, region: "eastasia"),
        Row("MAI-Cyber-1-Flash Inp glbl 1M Tokens", 0.4528m, region: "canadacentral"),
    ];

    [Fact]
    public void Luna_matches_its_standard_short_context_meters_not_the_older_5_6_luna()
    {
        var price = PriceMatcher.Match(Rows, ["gpt-6-luna-2026-09-01", "gpt-6-luna"]);

        Assert.NotNull(price);
        Assert.Equal(0.0755m, price.InputPerMillion);
        Assert.Equal(0.3774m, price.OutputPerMillion);
    }

    [Fact]
    public void Gpt_4o_uses_the_regional_meters_for_the_release_foundry_reports_in_pounds_per_million()
    {
        var price = PriceMatcher.Match(Rows, ["gpt-4o-2024-11-20", "gpt-4o"]);

        Assert.NotNull(price);
        Assert.Equal(2.3m, price.InputPerMillion); // £0.0023 per 1K
        Assert.Equal(9.1m, price.OutputPerMillion);
        Assert.Contains("gpt 4o 1120", price.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deployment_name_alone_still_finds_the_dearest_standard_gpt_4o_meter()
    {
        var price = PriceMatcher.Match(Rows, ["gpt-4o"]);

        Assert.NotNull(price);
        Assert.Equal(2.3m, price.InputPerMillion); // regional wins over the cheaper and dearer global releases
    }

    [Fact]
    public void Deepseek_matches_its_global_meters_from_the_foundry_name()
    {
        var price = PriceMatcher.Match(Rows, ["DeepSeek-V4.1-Flash"]);

        Assert.NotNull(price);
        Assert.Equal(0.2264m, price.InputPerMillion);
        Assert.Equal(0.9057m, price.OutputPerMillion);
    }

    [Fact]
    public void Mini_realtime_cached_batch_and_other_variants_are_never_the_price()
    {
        var mini = PriceMatcher.Match(Rows, ["gpt-4o-mini"]);

        Assert.NotNull(mini);
        Assert.Equal(0.1m, mini.InputPerMillion);
        Assert.Equal(0.5m, mini.OutputPerMillion);

        // Without the real input meter, the cheaper cached, batch and mini meters don't stand in for it.
        var noInput = Rows.Where(r => !r.MeterName.StartsWith("gpt 4o 1120 Inp", StringComparison.Ordinal)).ToList();
        Assert.NotEqual(0.0011m, PriceMatcher.Match(noInput, ["gpt-4o-2024-11-20"])?.InputPerMillion / 1000);
        Assert.NotEqual(0.1m, PriceMatcher.Match(noInput, ["gpt-4o-2024-11-20"])?.InputPerMillion);
    }

    [Fact]
    public void A_model_with_no_meter_or_only_half_a_price_is_unknown_not_an_error()
    {
        Assert.Null(PriceMatcher.Match(Rows, ["some-future-model"]));
        Assert.Null(PriceMatcher.Match(Rows, []));
        Assert.Null(PriceMatcher.Match(Rows.Where(r => !r.MeterName.Contains("Outp", StringComparison.Ordinal)), ["gpt-4o-2024-11-20"]));
        Assert.Null(PriceMatcher.Match([Row("x model Inp Tokens", 0m)], ["x-model"])); // a zero price isn't a price
    }

    [Theory]
    [InlineData("gpt-6-luna", "luna")]
    [InlineData("gpt-4o-2024-11-20", "4o")]
    [InlineData("DeepSeek-V4.1-Flash", "Flash")]
    public void The_search_word_is_the_most_distinctive_part_of_the_name(string name, string expected) =>
        Assert.Equal(expected, PriceMatcher.SearchWord([name]), ignoreCase: true);

    [Fact]
    public void Release_dates_come_off_model_names()
    {
        Assert.Equal("gpt-4o", PriceMatcher.Strip("gpt-4o-2024-11-20"));
        Assert.Equal("gpt-6-luna", PriceMatcher.Strip("gpt-6-luna-2026-09-01"));
        Assert.Equal("gpt-6-luna", PriceMatcher.Strip("gpt-6-luna"));
    }

    // ---- The lookup

    private sealed class Prices(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Page(params RetailRow[] rows) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject
        {
            ["Items"] = new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject
            {
                ["meterName"] = r.MeterName, ["armRegionName"] = r.ArmRegionName, ["retailPrice"] = r.RetailPrice, ["unitOfMeasure"] = r.UnitOfMeasure,
            })]),
            ["NextPageLink"] = null,
        }.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task The_lookup_asks_the_public_price_list_once_a_day_and_remembers_the_answer()
    {
        var handler = new Prices(() => Page(Rows));
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
        var lookup = new RetailPriceLookup(new HttpClient(handler), clock, NullLogger<RetailPriceLookup>.Instance);

        var first = await lookup.LookupAsync(["gpt-6-luna"], TestContext.Current.CancellationToken);
        var second = await lookup.LookupAsync(["gpt-6-luna"], TestContext.Current.CancellationToken);
        clock.Advance(RetailPriceLookup.CacheFor + TimeSpan.FromMinutes(1));
        await lookup.LookupAsync(["gpt-6-luna"], TestContext.Current.CancellationToken);

        Assert.Equal(0.0755m, first?.InputPerMillion);
        Assert.Equal(first, second);
        Assert.Equal(2, handler.Requests.Count); // the day-old answer was asked for again
        Assert.Contains("currencyCode=GBP", handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains("luna", Uri.UnescapeDataString(handler.Requests[0]), StringComparison.Ordinal);
        Assert.DoesNotContain("gpt-6-luna", Uri.UnescapeDataString(handler.Requests[0]), StringComparison.Ordinal); // only the search word leaves
    }

    [Fact]
    public async Task A_price_service_that_is_down_or_unreadable_gives_unknown_and_is_tried_again_next_time()
    {
        var failing = true;
        var handler = new Prices(() => failing ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Page(Rows));
        var lookup = new RetailPriceLookup(new HttpClient(handler), TimeProvider.System, NullLogger<RetailPriceLookup>.Instance);

        Assert.Null(await lookup.LookupAsync(["gpt-6-luna"], TestContext.Current.CancellationToken));
        failing = false;
        Assert.NotNull(await lookup.LookupAsync(["gpt-6-luna"], TestContext.Current.CancellationToken));

        var garbled = new RetailPriceLookup(
            new HttpClient(new Prices(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") })),
            TimeProvider.System, NullLogger<RetailPriceLookup>.Instance);
        Assert.Null(await garbled.LookupAsync(["gpt-6-luna"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_model_nobody_prices_is_unknown_and_that_is_remembered_too()
    {
        var handler = new Prices(() => Page(Rows));
        var lookup = new RetailPriceLookup(new HttpClient(handler), TimeProvider.System, NullLogger<RetailPriceLookup>.Instance);

        Assert.Null(await lookup.LookupAsync(["totally-unknown-model"], TestContext.Current.CancellationToken));
        var asked = handler.Requests.Count;
        Assert.Null(await lookup.LookupAsync(["totally-unknown-model"], TestContext.Current.CancellationToken));

        Assert.Equal(asked, handler.Requests.Count);
    }
}
