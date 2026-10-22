using System.Diagnostics;
using System.Globalization;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// One Cosmos call as measured (#115): a point operation, or a whole query (every page). Only names and numbers:
/// never document content, ids or query parameters.
/// </summary>
/// <param name="Operation">Stable and readable, e.g. <c>inventoryItems.inStock</c> or <c>products.read</c>.</param>
/// <param name="Status">The HTTP status (429 when Cosmos throttled it past the SDK's retries); 0 when the call threw first.</param>
public sealed record CosmosCall(
    string Operation, string Container, bool CrossPartition, int Status, double RequestUnits, int Items, int Pages, TimeSpan Duration);

/// <summary>Where measured calls go: App Insights metrics in the apps, a list in tests.</summary>
public interface ICosmosCallSink
{
    void Record(CosmosCall measured);
}

/// <summary>
/// The Cosmos RUs and calls of one function invocation (#115): an HTTP request, or a timer, queue or change-feed run.
/// The worker middleware starts one per invocation; every call made under it (its async flow) adds to it.
/// </summary>
public sealed class CosmosUsage
{
    private static readonly AsyncLocal<CosmosUsage?> CurrentUsage = new();

    private long calls;
    private long centiRequestUnits; // RUs × 100, so Interlocked can add them

    /// <summary>The invocation's totals, or null outside one (startup, tests).</summary>
    public static CosmosUsage? Current => CurrentUsage.Value;

    public int Calls => (int)Interlocked.Read(ref calls);

    public double RequestUnits => Interlocked.Read(ref centiRequestUnits) / 100.0;

    /// <summary>Starts counting for this async flow (and what it starts); dispose to stop.</summary>
    public static Scope Begin()
    {
        var usage = new CosmosUsage();
        var previous = CurrentUsage.Value;
        CurrentUsage.Value = usage;
        return new Scope(usage, previous);
    }

    public void Add(double requestUnits)
    {
        Interlocked.Increment(ref calls);
        Interlocked.Add(ref centiRequestUnits, (long)Math.Round(requestUnits * 100));
    }

    public sealed class Scope(CosmosUsage usage, CosmosUsage? previous) : IDisposable
    {
        public CosmosUsage Usage => usage;

        public void Dispose() => CurrentUsage.Value = previous;
    }
}

/// <summary>Records each call to the sink and to the invocation's totals.</summary>
public sealed class CosmosRecorder(ICosmosCallSink sink)
{
    public void Record(CosmosCall call)
    {
        CosmosUsage.Current?.Add(call.RequestUnits);
        try
        {
            sink.Record(call);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Measuring must never break a Cosmos call.
            Trace.TraceWarning($"Couldn't record a Cosmos call: {e.Message}");
        }
    }
}

/// <summary>From app settings (#115): COSMOS_INDEX_METRICS (default false) asks Cosmos for index metrics on queries.</summary>
public sealed record CosmosTelemetrySettings(bool IndexMetrics = false)
{
    public static readonly TimeSpan IndexMetricsInterval = TimeSpan.FromHours(1);
}

/// <summary>
/// Measures every request the app's Cosmos client sends (#115), as a Cosmos SDK request handler, so no point read,
/// write, upsert, replace or delete is missed. A query's pages are left to <see cref="CosmosJson.QueryAsync"/>, which
/// records the whole query once (<see cref="QueryScope"/>). Also logs a query's index metrics at most once an hour per
/// operation while <see cref="CosmosTelemetrySettings.IndexMetrics"/> is on.
/// </summary>
public sealed class CosmosMetricsHandler(
    CosmosRecorder recorder, CosmosTelemetrySettings settings, TimeProvider clock, ILogger<CosmosMetricsHandler> logger) : RequestHandler
{
    private static readonly AsyncLocal<bool> InQuery = new();
    private readonly Dictionary<string, DateTimeOffset> indexMetricsLogged = new(StringComparer.Ordinal);

    public CosmosRecorder Recorder => recorder;

    public bool IndexMetrics => settings.IndexMetrics;

    /// <summary>While open, this flow's requests are a query's pages: the query records itself.</summary>
    public static IDisposable QueryScope()
    {
        InQuery.Value = true;
        return new EndQuery();
    }

    public override async Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
    {
        if (InQuery.Value)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var started = clock.GetTimestamp();
        var status = 0;
        double charge = 0;
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            charge = response.Headers.RequestCharge;
            return response;
        }
        finally
        {
            var container = ContainerOf(request.RequestUri);
            var verb = VerbOf(request);
            recorder.Record(new CosmosCall(
                $"{container}.{verb}",
                container,
                verb == "query" && request.Headers["x-ms-documentdb-partitionkey"] is null,
                status,
                charge,
                status is >= 200 and < 300 && verb != "delete" ? 1 : 0,
                1,
                clock.GetElapsedTime(started)));
        }
    }

    /// <summary>Logs a query's index metrics (index paths only, never values) at most once per interval per operation.</summary>
    public void IndexMetricsSeen(string operation, string? metrics)
    {
        if (!settings.IndexMetrics || string.IsNullOrWhiteSpace(metrics))
        {
            return;
        }

        var now = clock.GetUtcNow();
        lock (indexMetricsLogged)
        {
            if (indexMetricsLogged.TryGetValue(operation, out var last) && now - last < CosmosTelemetrySettings.IndexMetricsInterval)
            {
                return;
            }

            indexMetricsLogged[operation] = now;
        }

        CosmosLog.IndexMetrics(logger, operation, metrics);
    }

    /// <summary>The container from a request's path: <c>dbs/{db}/colls/{container}/…</c>.</summary>
    internal static string ContainerOf(Uri? uri)
    {
        var parts = (uri?.OriginalString ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(parts, "colls");
        return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : "database";
    }

    internal static string VerbOf(RequestMessage request) =>
        request.Method.Method switch
        {
            "GET" => "read",
            "PUT" => "replace",
            "DELETE" => "delete",
            "PATCH" => "patch",
            "POST" when string.Equals(request.Headers["x-ms-documentdb-isquery"], "true", StringComparison.OrdinalIgnoreCase)
                || request.Headers["x-ms-documentdb-query-iscontinuationexpected"] is not null => "query",
            "POST" when string.Equals(request.Headers["x-ms-documentdb-is-upsert"], "true", StringComparison.OrdinalIgnoreCase) => "upsert",
            "POST" => "create",
            var other => other.ToLowerInvariant(),
        };

    private sealed class EndQuery : IDisposable
    {
        public void Dispose() => InQuery.Value = false;
    }
}

/// <summary>Sends each call to App Insights as pre-aggregated metrics (one row per minute per dimension set, not one per call).</summary>
public sealed class AppInsightsCosmosCallSink(TelemetryClient telemetry) : ICosmosCallSink
{
    private const string Operation = "operation";
    private const string Container = "container";
    private const string Partition = "partition";
    private const string Status = "status";

    public void Record(CosmosCall measured)
    {
        var call = measured;
        var scope = call.CrossPartition ? "cross" : "single";
        var status = call.Status.ToString(CultureInfo.InvariantCulture);
        Track("Cosmos RU", call.RequestUnits);
        Track("Cosmos duration ms", call.Duration.TotalMilliseconds);
        Track("Cosmos items", call.Items);
        Track("Cosmos pages", call.Pages);

        void Track(string name, double value) =>
            telemetry.GetMetric(name, Operation, Container, Partition, Status)
                .TrackValue(value, call.Operation, call.Container, scope, status);
    }
}

/// <summary>Where nothing is sent (no App Insights, e.g. tests and local runs without a connection string).</summary>
public sealed class NoCosmosCallSink : ICosmosCallSink
{
    public void Record(CosmosCall measured)
    {
    }
}

/// <summary>Per invocation (#115): totals as a metric by function, and one trace correlated with the request.</summary>
public sealed class CosmosInvocationReporter(ILogger<CosmosInvocationReporter> logger, TelemetryClient? telemetry = null)
{
    public void Report(string function, string trigger, CosmosUsage usage)
    {
        if (usage.Calls == 0)
        {
            return;
        }

        telemetry?.GetMetric("Cosmos RU per invocation", "function", "trigger").TrackValue(usage.RequestUnits, function, trigger);
        telemetry?.GetMetric("Cosmos calls per invocation", "function", "trigger").TrackValue(usage.Calls, function, trigger);
        CosmosLog.Invocation(logger, function, trigger, usage.Calls, Math.Round(usage.RequestUnits, 2));
    }
}

internal static partial class CosmosLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Cosmos index metrics for {CosmosOperation}: {IndexMetrics}")]
    public static partial void IndexMetrics(ILogger logger, string cosmosOperation, string indexMetrics);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Cosmos usage: {Function} ({Trigger}) made {CosmosCalls} Cosmos calls costing {CosmosRu} RU.")]
    public static partial void Invocation(ILogger logger, string function, string trigger, int cosmosCalls, double cosmosRu);
}
