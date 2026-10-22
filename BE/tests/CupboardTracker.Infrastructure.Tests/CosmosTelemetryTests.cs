using System.Net;
using System.Text;
using CupboardTracker.Infrastructure.Cosmos;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CupboardTracker.Infrastructure.Tests;

/// <summary>Measuring Cosmos (#115): every call's RUs, items, pages, scope and status; totals per invocation; index metrics.</summary>
public sealed class CosmosTelemetryTests
{
    private sealed class ListSink : ICosmosCallSink
    {
        public List<CosmosCall> Calls { get; } = [];

        public void Record(CosmosCall measured) => Calls.Add(measured);
    }

    private sealed class Logs : ILogger<CosmosMetricsHandler>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    /// <summary>The rest of the Cosmos pipeline: answers with a status and a charge, or throws.</summary>
    private sealed class Backend(HttpStatusCode status, double charge, Exception? throws = null) : RequestHandler
    {
        public override Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
        {
            if (throws is not null)
            {
                throw throws;
            }

            var response = new ResponseMessage(status, request);
            response.Headers["x-ms-request-charge"] = charge.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(response);
        }
    }

    /// <summary>A query's pages, each with its charge and documents.</summary>
    private sealed class Pages(params (double Charge, int Documents, HttpStatusCode Status)[] pages) : FeedIterator
    {
        private int next;

        public override bool HasMoreResults => next < pages.Length;

        public override Task<ResponseMessage> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            var (charge, documents, status) = pages[next++];
            var body = "{\"Documents\":[" + string.Join(",", Enumerable.Range(0, documents).Select(i => $"{{\"id\":\"{i}\"}}")) + "]}";
            var response = new ResponseMessage(status) { Content = new MemoryStream(Encoding.UTF8.GetBytes(body)) };
            response.Headers["x-ms-request-charge"] = charge.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(response);
        }
    }

    private readonly ListSink sink = new();
    private readonly Logs logs = new();
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    private CosmosMetricsHandler Handler(bool indexMetrics = false, RequestHandler? backend = null) =>
        new(new CosmosRecorder(sink), new CosmosTelemetrySettings(indexMetrics), clock, logs) { InnerHandler = backend ?? new Backend(HttpStatusCode.OK, 1) };

    private static RequestMessage Request(HttpMethod method, string path, params (string Name, string Value)[] headers)
    {
        var request = new RequestMessage(method, new Uri(path, UriKind.Relative));
        foreach (var (name, value) in headers)
        {
            request.Headers[name] = value;
        }

        return request;
    }

    [Fact]
    public async Task A_point_read_is_recorded_with_its_container_verb_charge_and_status()
    {
        var handler = Handler(backend: new Backend(HttpStatusCode.OK, 1.29));

        await handler.SendAsync(Request(HttpMethod.Get, "dbs/cupboard/colls/products/docs/5f0c"), CancellationToken.None);

        var call = Assert.Single(sink.Calls);
        Assert.Equal(("products.read", "products", false, 200, 1.29, 1, 1), (call.Operation, call.Container, call.CrossPartition, call.Status, call.RequestUnits, call.Items, call.Pages));
    }

    [Fact]
    public async Task A_miss_a_throttle_and_a_failure_are_recorded_too()
    {
        await Handler(backend: new Backend(HttpStatusCode.NotFound, 1)).SendAsync(Request(HttpMethod.Get, "dbs/cupboard/colls/units/docs/x"), CancellationToken.None);
        await Handler(backend: new Backend((HttpStatusCode)429, 0)).SendAsync(Request(HttpMethod.Put, "dbs/cupboard/colls/units/docs/x"), CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Handler(backend: new Backend(HttpStatusCode.OK, 0, new HttpRequestException("down"))).SendAsync(Request(HttpMethod.Delete, "dbs/cupboard/colls/units/docs/x"), CancellationToken.None));

        Assert.Equal([("units.read", 404, 0), ("units.replace", 429, 0), ("units.delete", 0, 0)], sink.Calls.Select(c => (c.Operation, c.Status, c.Items)));
    }

    [Theory]
    [InlineData("POST", "x-ms-documentdb-is-upsert", "True", "upsert")]
    [InlineData("POST", "x-ms-documentdb-isquery", "True", "query")]
    [InlineData("POST", null, null, "create")]
    [InlineData("PATCH", null, null, "patch")]
    [InlineData("DELETE", null, null, "delete")]
    public void Each_request_gets_a_verb(string method, string? header, string? value, string verb)
    {
        var request = header is null ? Request(new HttpMethod(method), "dbs/d/colls/c/docs") : Request(new HttpMethod(method), "dbs/d/colls/c/docs", (header, value!));
        Assert.Equal(verb, CosmosMetricsHandler.VerbOf(request));
    }

    [Fact]
    public async Task A_query_not_through_the_helper_is_cross_partition_unless_it_names_a_partition()
    {
        var handler = Handler();
        await handler.SendAsync(Request(HttpMethod.Post, "dbs/d/colls/inventoryItems/docs", ("x-ms-documentdb-isquery", "True")), CancellationToken.None);
        await handler.SendAsync(Request(HttpMethod.Post, "dbs/d/colls/inventoryItems/docs", ("x-ms-documentdb-isquery", "True"), ("x-ms-documentdb-partitionkey", "[\"p\"]")), CancellationToken.None);

        Assert.Equal([true, false], sink.Calls.Select(c => c.CrossPartition));
    }

    [Fact]
    public async Task A_query_through_the_helper_is_one_call_over_every_page()
    {
        var handler = Handler();
        using (CosmosMetricsHandler.QueryScope())
        {
            await handler.SendAsync(Request(HttpMethod.Post, "dbs/d/colls/inventoryItems/docs", ("x-ms-documentdb-isquery", "True")), CancellationToken.None);
        }

        Assert.Empty(sink.Calls); // the page itself isn't recorded: the helper records the whole query

        var rows = await CosmosJson.ReadAllAsync(
            new Pages((2.5, 100, HttpStatusCode.OK), (3.5, 100, HttpStatusCode.OK), (1, 7, HttpStatusCode.OK)),
            "inventoryItems.inStock", "inventoryItems", crossPartition: true, max: null, new CosmosRecorder(sink), handler, CancellationToken.None);

        Assert.Equal(207, rows.Count);
        var call = Assert.Single(sink.Calls);
        Assert.Equal(("inventoryItems.inStock", true, 200, 7.0, 207, 3), (call.Operation, call.CrossPartition, call.Status, call.RequestUnits, call.Items, call.Pages));
    }

    [Fact]
    public async Task A_query_that_fails_part_way_is_still_recorded_with_its_status()
    {
        await Assert.ThrowsAsync<CosmosException>(() => CosmosJson.ReadAllAsync(
            new Pages((2, 10, HttpStatusCode.OK), (0, 0, (HttpStatusCode)429)),
            "products.needingShelfLife", "products", crossPartition: true, max: null, new CosmosRecorder(sink), null, CancellationToken.None));

        var call = Assert.Single(sink.Calls);
        Assert.Equal((429, 2.0, 10, 2), (call.Status, call.RequestUnits, call.Items, call.Pages));
    }

    [Theory]
    [InlineData("inventoryItems", "GetInStockWithoutExpiryAsync", "inventoryItems.inStockWithoutExpiry")]
    [InlineData("pushSubscriptions", "ListAsync", "pushSubscriptions.list")]
    [InlineData("products", "Getaway", "products.getaway")]
    [InlineData("products", "", "products.query")]
    public void Query_names_come_from_the_container_and_the_calling_method(string container, string caller, string name) =>
        Assert.Equal(name, CosmosJson.OperationName(container, caller));

    [Fact]
    public void The_container_comes_from_the_path()
    {
        Assert.Equal("shoppingList", CosmosMetricsHandler.ContainerOf(new Uri("dbs/cupboard/colls/shoppingList/docs/abc", UriKind.Relative)));
        Assert.Equal("database", CosmosMetricsHandler.ContainerOf(new Uri("dbs/cupboard", UriKind.Relative)));
    }

    // ---- Per invocation

    [Fact]
    public async Task Each_invocation_counts_only_its_own_calls_even_when_they_run_at_once()
    {
        var recorder = new CosmosRecorder(sink);

        async Task<CosmosUsage> Invocation(int calls, double each)
        {
            using var scope = CosmosUsage.Begin();
            for (var i = 0; i < calls; i++)
            {
                await Task.Yield();
                recorder.Record(new CosmosCall("units.read", "units", false, 200, each, 1, 1, TimeSpan.Zero));
            }

            return scope.Usage;
        }

        var results = await Task.WhenAll(Invocation(3, 1.5), Invocation(10, 2), Invocation(1, 7.25));

        Assert.Equal([(3, 4.5), (10, 20.0), (1, 7.25)], results.Select(u => (u.Calls, u.RequestUnits)));
        Assert.Null(CosmosUsage.Current);
    }

    private sealed class ReporterLogs : ILogger<CosmosInvocationReporter>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void An_invocation_reports_its_totals_once_and_says_nothing_when_it_made_no_calls()
    {
        var reporterLogs = new ReporterLogs();
        var reporter = new CosmosInvocationReporter(reporterLogs);
        using (var quiet = CosmosUsage.Begin())
        {
            reporter.Report("ping", "http", quiet.Usage);
        }

        using (var busy = CosmosUsage.Begin())
        {
            busy.Usage.Add(2.86);
            busy.Usage.Add(1);
            reporter.Report("scanners", "http", busy.Usage);
        }

        Assert.Equal(["Cosmos usage: scanners (http) made 2 Cosmos calls costing 3.86 RU."], reporterLogs.Messages);
    }

    // ---- Index metrics

    [Fact]
    public void Index_metrics_are_logged_only_when_on_and_at_most_hourly_per_operation()
    {
        Handler(indexMetrics: false).IndexMetricsSeen("products.list", "Utilized Single Indexes: /type/?");
        Assert.Empty(logs.Messages);

        var handler = Handler(indexMetrics: true);
        handler.IndexMetricsSeen("products.list", "Utilized Single Indexes: /type/?");
        handler.IndexMetricsSeen("products.list", "Utilized Single Indexes: /type/?");
        handler.IndexMetricsSeen("units.inStock", "Potential Single Indexes: /status/?");
        clock.Advance(TimeSpan.FromMinutes(59));
        handler.IndexMetricsSeen("products.list", "x");
        clock.Advance(TimeSpan.FromMinutes(2));
        handler.IndexMetricsSeen("products.list", "Utilized Single Indexes: /type/?");

        Assert.Equal(3, logs.Messages.Count);
        Assert.StartsWith("Cosmos index metrics for products.list:", logs.Messages[0], StringComparison.Ordinal);
        Assert.StartsWith("Cosmos index metrics for units.inStock:", logs.Messages[1], StringComparison.Ordinal);
    }

    // ---- What reaches App Insights

    private sealed class Channel : ITelemetryChannel
    {
        public List<ITelemetry> Sent { get; } = [];

        public bool? DeveloperMode { get; set; }

        public string EndpointAddress { get; set; } = "";

        public void Send(ITelemetry item) => Sent.Add(item);

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void App_insights_gets_aggregated_metrics_with_names_and_numbers_only()
    {
        var channel = new Channel();
        using var configuration = new TelemetryConfiguration { TelemetryChannel = channel, ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000" };
        var telemetry = new TelemetryClient(configuration);
        var appInsights = new AppInsightsCosmosCallSink(telemetry);

        for (var i = 0; i < 50; i++)
        {
            appInsights.Record(new CosmosCall("units.inStock", "inventoryItems", true, 200, 2, 3, 1, TimeSpan.FromMilliseconds(10)));
        }

        telemetry.Flush();

        var metrics = channel.Sent.OfType<MetricTelemetry>().ToList();
        string[] names = ["Cosmos RU", "Cosmos duration ms", "Cosmos items", "Cosmos pages"];
        Assert.Equal(names.Order(StringComparer.Ordinal), metrics.Select(m => m.Name).Order(StringComparer.Ordinal));
        var ru = metrics.Single(m => m.Name == "Cosmos RU");
        Assert.Equal((50, 100.0), (ru.Count, ru.Sum)); // one aggregate, not 50 rows
        Assert.Equal(
            new Dictionary<string, string> { ["operation"] = "units.inStock", ["container"] = "inventoryItems", ["partition"] = "cross", ["status"] = "200" },
            ru.Properties.Where(p => p.Key is "operation" or "container" or "partition" or "status").ToDictionary());
    }
}
