using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Infrastructure.ModelTesting;

/// <summary>A model's public list price in pounds for a million tokens, and the meters it came from.</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion, string Basis);

public interface IModelPrices
{
    /// <summary>The list price for a model, by the names it goes by (Foundry's reported model, the deployment name), or null
    /// when none can be matched or the price service can't be reached. Never throws.</summary>
    Task<ModelPrice?> LookupAsync(IReadOnlyList<string> names, CancellationToken cancellationToken);
}

/// <summary>One row of the Azure Retail Prices API.</summary>
public sealed record RetailRow(string MeterName, string ArmRegionName, decimal RetailPrice, string UnitOfMeasure);

/// <summary>
/// Matches a model to its Standard pay-as-you-go token meters among the Retail Prices rows (#121). The meter names are
/// inconsistent (<c>gpt 4o 1120 Inp regnl Tokens</c>, <c>6-luna ShortCo Inp Std Gl 1M Tokens</c>, <c>V4.1 Flash Input Glbl
/// 1M Tokens</c>), so a meter matches when its name, with only letters and digits kept, starts with the model's name and
/// what follows isn't a variant (mini, realtime, audio…); cached, batch, flex, provisioned, long-context and data-zone
/// meters are left out. Regional UK South pricing beats global; when several meters still match, the dearest is used, so
/// an estimate is never lower than the truth.
/// </summary>
public static partial class PriceMatcher
{
    private static readonly string[] Variants =
        ["mini", "nano", "pro", "turbo", "rt", "realtime", "aud", "audio", "tts", "mn", "search", "image", "img", "transcribe", "ft", "finetune"];

    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "cached", "cchd", "cd", "wr", "batch", "bch", "flex", "fl", "pp", "prvw", "preview", "aud", "audio", "rt", "realtime",
        "longco", "loco", "lo", "dz", "datazone", "img", "image", "tts", "ft",
    };

    /// <summary>The word to ask the price service for: the model name's most distinctive part ("luna", "4o", "flash").</summary>
    public static string? SearchWord(IReadOnlyList<string> names) =>
        names.Select(Strip).SelectMany(n => Words(n)).Where(w => w is not ("gpt" or "deepseek" or "openai") && !w.All(char.IsDigit))
            .OrderByDescending(w => w.Length).FirstOrDefault();

    public static ModelPrice? Match(IEnumerable<RetailRow> rows, IReadOnlyList<string> names)
    {
        var keys = names.Select(Strip).Select(Normalise).Where(k => k.Length >= 3).Distinct().ToList();
        keys.AddRange(keys.Select(k => k.StartsWith("gpt", StringComparison.Ordinal) ? k[3..] : k).Where(k => k.Length >= 3).ToList());
        keys.AddRange(keys.Select(k => k.StartsWith("deepseek", StringComparison.Ordinal) ? k[8..] : k).Where(k => k.Length >= 3).ToList());
        keys = [.. keys.Distinct()];

        var candidates = rows
            .Where(r => r.RetailPrice > 0 && Scale(r.UnitOfMeasure) is not null && IsStandard(r.MeterName) && keys.Any(k => Starts(r.MeterName, k)))
            .Select(r => (Row: r, Kind: Kind(r.MeterName)))
            .Where(x => x.Kind is not null)
            .ToList();

        var versions = names.Select(n => Release().Match(n)).Where(m => m.Success).Select(m => m.Groups[2].Value + m.Groups[3].Value).Distinct().ToList();
        var input = Pick(candidates.Where(x => x.Kind == "in").Select(x => x.Row), versions);
        var output = Pick(candidates.Where(x => x.Kind == "out").Select(x => x.Row), versions);
        return input is null || output is null
            ? null
            : new ModelPrice(PerMillion(input), PerMillion(output), $"{input.MeterName} / {output.MeterName}");
    }

    private static RetailRow? Pick(IEnumerable<RetailRow> rows, IReadOnlyList<string> versions)
    {
        var list = rows.ToList();
        // A meter for the release Foundry reports ("gpt-4o-2024-11-20" → 1120) beats other releases of the same model.
        if (list.Where(r => versions.Any(v => Normalise(r.MeterName).Contains(v, StringComparison.Ordinal))).ToList() is { Count: > 0 } sameRelease)
        {
            list = sameRelease;
        }

        var regional = list.Where(r => string.Equals(r.ArmRegionName, "uksouth", StringComparison.OrdinalIgnoreCase)
            && Words(r.MeterName).Any(w => w is "regnl" or "regional")).ToList();
        var pool = regional.Count > 0 ? regional : list;
        return pool.OrderByDescending(PerMillion).FirstOrDefault();
    }

    private static decimal PerMillion(RetailRow row) => row.RetailPrice * Scale(row.UnitOfMeasure)!.Value;

    private static decimal? Scale(string unit) => unit.Trim().ToUpperInvariant() switch
    {
        "1M" => 1m,
        "1K" => 1000m,
        _ => null,
    };

    private static bool IsStandard(string meter) =>
        !Words(meter).Any(Excluded.Contains) && meter.Contains("Tokens", StringComparison.OrdinalIgnoreCase);

    private static string? Kind(string meter)
    {
        var words = Words(meter);
        return words.Any(w => w is "inp" or "input" or "in") ? "in"
            : words.Any(w => w is "opt" or "outp" or "output" or "out") ? "out"
            : null;
    }

    private static bool Starts(string meter, string key)
    {
        var normal = Normalise(meter);
        if (!normal.StartsWith(key, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = normal[key.Length..];
        return !Variants.Any(v => rest.StartsWith(v, StringComparison.Ordinal));
    }

    /// <summary>Takes a trailing release date off a model name ("gpt-4o-2024-11-20" → "gpt-4o").</summary>
    internal static string Strip(string name) => DateSuffix().Replace(name.Trim(), "");

    private static string Normalise(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string[] Words(string text) =>
        text.ToLowerInvariant().Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"(\d{4})-(\d{2})-(\d{2})", RegexOptions.None, 1000)]
    private static partial Regex Release();

    [GeneratedRegex(@"[-_ ]?\d{4}-\d{2}-\d{2}$|[-_ ]\d{4}$", RegexOptions.None, 1000)]
    private static partial Regex DateSuffix();
}

/// <summary>
/// Looks prices up in the public Azure Retail Prices API (#121; no key, no household data sent) and keeps each answer for a
/// day, found or not. Asks for UK South first, then any region (global meters cost the same everywhere).
/// </summary>
public sealed partial class RetailPriceLookup(HttpClient http, TimeProvider clock, ILogger<RetailPriceLookup> logger) : IModelPrices
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromDays(1);

    private const string Endpoint = "https://prices.azure.com/api/retail/prices";
    private const int MaxPages = 8;

    private readonly Dictionary<string, (DateTimeOffset Until, ModelPrice? Price)> cache = [];
    private readonly Lock gate = new();

    public async Task<ModelPrice?> LookupAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var word = PriceMatcher.SearchWord(names);
        if (word is null)
        {
            return null;
        }

        var key = string.Join('|', names.Select(n => n.ToLowerInvariant()));
        lock (gate)
        {
            if (cache.TryGetValue(key, out var hit) && clock.GetUtcNow() < hit.Until)
            {
                return hit.Price;
            }
        }

        try
        {
            var price = await FindAsync(names, word, regional: true, cancellationToken)
                ?? await FindAsync(names, word, regional: false, cancellationToken);
            lock (gate)
            {
                cache[key] = (clock.GetUtcNow() + CacheFor, price);
            }

            return price;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (e is TaskCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            LogFailed(e.GetType().Name);
            return null; // not cached: try again next time
        }
    }

    private async Task<ModelPrice?> FindAsync(IReadOnlyList<string> names, string word, bool regional, CancellationToken cancellationToken)
    {
        var filter = $"serviceName eq 'Foundry Models' and priceType eq 'Consumption' and contains(meterName,'{word.Replace("'", "''")}')"
            + (regional ? " and armRegionName eq 'uksouth'" : "");
        var url = $"{Endpoint}?currencyCode=GBP&$filter={Uri.EscapeDataString(filter)}";
        var rows = new List<RetailRow>();
        for (var page = 0; page < MaxPages && url is not null; page++)
        {
            var body = JsonNode.Parse(await http.GetStringAsync(url, cancellationToken)) as JsonObject ?? throw new InvalidOperationException("Unexpected price response.");
            foreach (var item in body["Items"] as JsonArray ?? [])
            {
                if (item is JsonObject o && o["retailPrice"] is JsonValue price && price.TryGetValue<decimal>(out var amount))
                {
                    rows.Add(new RetailRow(
                        (string?)o["meterName"] ?? "", (string?)o["armRegionName"] ?? "", amount, (string?)o["unitOfMeasure"] ?? ""));
                }
            }

            url = (string?)body["NextPageLink"];
        }

        return PriceMatcher.Match(rows, names);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't look up a model's list price ({Problem}); the estimate will say unknown.")]
    private partial void LogFailed(string problem);
}
