using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using CupboardTracker.Application.Adding;
using CupboardTracker.Application.AiModels;
using CupboardTracker.Application.GenericNaming;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.ShelfLives;
using CupboardTracker.Infrastructure.GenericNaming;
using CupboardTracker.Infrastructure.Photos;
using CupboardTracker.Infrastructure.Recipes;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Infrastructure.ModelTesting;

/// <summary>The test image (#121), from the Function app's storage account.</summary>
public interface IModelTestImageStore
{
    Task<byte[]> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The saved checks for a new Foundry model (#121). Checks 1 to 3 ask the deployment small questions directly; 4 to 8 use the
/// app's own callers (the real recipe, Ready now, Adjust, photo and generic name requests, with their real validators) on
/// canned data, against this deployment alone and with no fallback; 9 and 10 read the numbers the calls left behind
/// (<see cref="AiCallLog"/>). Nothing is logged or saved but states, one-line reasons, times, token counts and costs.
/// </summary>
public sealed class FoundryModelTestSuite(
    IHttpClientFactory httpFactory, TokenCredential credential, RecipeGenerationOptions recipeOptions,
    PhotoRecognitionOptions photoOptions, GenericNameWriterOptions nameOptions, IModelTestImageStore images,
    IModelPrices prices, TimeProvider clock, ILoggerFactory loggers) : IModelTestSuite
{
    public static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan SlowCall = TimeSpan.FromSeconds(20);

    /// <summary>What gpt-4o used for the same requests (measured on the live app), for the "far more output tokens" flag.</summary>
    public const int Gpt4oRecipeCompletionTokens = 401;
    public const int Gpt4oReadyNowCompletionTokens = 263;
    public const double TokenWarnFactor = 3;

    private const string Ping = "Reply with the single word: ready";

    public async Task<SuiteOutcome> RunAsync(
        string deployment, Func<IReadOnlyList<CheckResult>, Task> progress, CancellationToken cancellationToken)
    {
        var results = CheckIds.All.ToDictionary(id => id, CheckResult.Waiting);
        var calls = new List<AiCall>();
        var started = Stopwatch.GetTimestamp();
        var model = new FixedActiveModels(deployment);
        var endpoint = recipeOptions.Endpoint;

        IReadOnlyList<CheckResult> Snapshot() => [.. CheckIds.All.Select(id => results[id])];

        async Task Do(string id, Func<Task<CheckResult>> body)
        {
            results[id] = new CheckResult(id, CheckStates.Running);
            await progress(Snapshot());
            using var scope = AiCallLog.Begin();
            var begun = Stopwatch.GetTimestamp();
            CheckResult result;
            try
            {
                result = await body();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new CheckResult(id, CheckStates.Fail, "It didn't answer in time.");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                result = new CheckResult(id, CheckStates.Fail, $"The check stopped unexpectedly ({e.GetType().Name}).");
            }

            var used = scope.Log.Calls;
            calls.AddRange(used);
            results[id] = result with
            {
                Id = id,
                Ms = result.Ms ?? (int)Stopwatch.GetElapsedTime(begun).TotalMilliseconds,
                PromptTokens = used.Sum(c => c.PromptTokens),
                CompletionTokens = used.Sum(c => c.CompletionTokens),
            };
            await progress(Snapshot());
        }

        async Task Skip(string id, string why)
        {
            results[id] = new CheckResult(id, CheckStates.Fail, $"Skipped: {why}", 0);
            await progress(Snapshot());
        }

        if (endpoint is null)
        {
            foreach (var id in CheckIds.All)
            {
                results[id] = new CheckResult(id, CheckStates.Fail, "Skipped: AI_SERVICES_ENDPOINT isn't set.", 0);
            }

            await progress(Snapshot());
            return new SuiteOutcome(Snapshot(), TemperatureMode.Usual, null, new RunCosts(null, null, null, null, null), 0);
        }

        var http = httpFactory.CreateClient();
        var probe = new FoundryProbe(http, endpoint, credential);

        await Do(CheckIds.Reachable, async () =>
        {
            var reply = await probe.ChatAsync(deployment, "You answer in one word.", Ping, null, null, CallLimit, cancellationToken);
            return reply.Ok ? Pass() : Fail(Explain(reply.Call));
        });

        if (results[CheckIds.Reachable].State == CheckStates.Fail)
        {
            foreach (var id in CheckIds.All.Skip(1).Where(id => id is not (CheckIds.Speed or CheckIds.Cost)))
            {
                await Skip(id, "the model wasn't reachable.");
            }
        }
        else
        {
            await Do(CheckIds.Temperature, () => TemperatureAsync(probe, deployment, model, cancellationToken));
            if (results[CheckIds.Temperature].State == CheckStates.Fail)
            {
                foreach (var id in CheckIds.All.Skip(2).Where(id => id is not (CheckIds.Speed or CheckIds.Cost)))
                {
                    await Skip(id, "the temperature check failed.");
                }
            }
            else
            {
                await Do(CheckIds.StrictSchema, () => StrictSchemaAsync(probe, deployment, model, cancellationToken));
                if (results[CheckIds.StrictSchema].State == CheckStates.Fail)
                {
                    foreach (var id in CheckIds.All.Skip(3).Where(id => id is not (CheckIds.Speed or CheckIds.Cost)))
                    {
                        await Skip(id, "the strict JSON schema check failed.");
                    }
                }
                else
                {
                    await RealCallersAsync(deployment, http, model, Do, Skip, cancellationToken);
                }
            }
        }

        await Do(CheckIds.Speed, () => Task.FromResult(Speed(calls)));
        var costs = await CostsAsync(deployment, calls, results, cancellationToken);
        await Do(CheckIds.Cost, () => Task.FromResult(CostCheck(costs, results)));
        var version = calls.Select(c => c.ModelVersion).FirstOrDefault(v => v is not null);
        return new SuiteOutcome(Snapshot(), model.Mode, version, costs.Costs, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private async Task RealCallersAsync(
        string deployment, HttpClient http, FixedActiveModels model, Func<string, Func<Task<CheckResult>>, Task> run,
        Func<string, string, Task> skip, CancellationToken cancellationToken)
    {
        var generator = new FoundryRecipeGenerator(
            http, recipeOptions with { Deployment = deployment, Attempts = 2 }, credential, clock, loggers.CreateLogger<FoundryRecipeGenerator>(), model);
        Recipe? recipe = null;

        await run(CheckIds.Recipe, async () =>
        {
            switch (await generator.SuggestAsync(CannedStock.HalfAndHalf, cancellationToken))
            {
                case RecipeResult.Suggested s:
                    recipe = s.Recipe;
                    return s.Recipe.Warnings is { Count: > 0 } w
                        ? new CheckResult(CheckIds.Recipe, CheckStates.Warn, $"A valid recipe, but the checks raised {w.Count} warning{(w.Count == 1 ? "" : "s")}.")
                        : Pass();
                case RecipeResult.Unavailable u:
                    return Fail(Reason(u.Reason));
                default:
                    return Fail("No recipe came back.");
            }
        });

        await run(CheckIds.ReadyNow, async () => await generator.SuggestQuickIdeasAsync(CannedStock.ReadyNow, cancellationToken) switch
        {
            QuickIdeasResult.Suggested { Ideas.Count: >= FoundryRecipeGenerator.MinIdeas } => Pass(),
            QuickIdeasResult.Suggested s => new CheckResult(CheckIds.ReadyNow, CheckStates.Warn,
                $"Only {s.Ideas.Count} usable idea{(s.Ideas.Count == 1 ? "" : "s")} came back ({FoundryRecipeGenerator.MinIdeas} to {FoundryRecipeGenerator.MaxIdeas} wanted)."),
            QuickIdeasResult.Unavailable u => Fail(Reason(u.Reason)),
            _ => Fail("No ideas came back."),
        });

        if (recipe is null)
        {
            await skip(CheckIds.Adjust, "there was no recipe to adjust.");
        }
        else
        {
            var changes = new RecipeChanges([], ["chilli flakes"], [], null);
            await run(CheckIds.Adjust, async () => await generator.AdjustAsync(new RecipeAdjustmentRequest(CannedStock.HalfAndHalf, recipe, changes), cancellationToken) switch
            {
                RecipeResult.Suggested { Recipe: { CouldNotAddTyped.Count: > 0 } } => new CheckResult(CheckIds.Adjust, CheckStates.Warn, "It didn't add what was asked."),
                RecipeResult.Suggested { Recipe: { Warnings.Count: > 0 } } => new CheckResult(CheckIds.Adjust, CheckStates.Warn, "An adjusted recipe, but the checks raised warnings."),
                RecipeResult.Suggested => Pass(),
                RecipeResult.Unavailable u => Fail(Reason(u.Reason)),
                _ => Fail("No recipe came back."),
            });
        }

        await run(CheckIds.Photo, async () =>
        {
            var recogniser = new FoundryPhotoRecogniser(
                http, photoOptions with { Deployment = deployment, Attempts = 2 }, credential, clock, loggers.CreateLogger<FoundryPhotoRecogniser>(), model);
            var image = await images.GetAsync(cancellationToken);
            return await recogniser.RecogniseAsync(image, cancellationToken) switch
            {
                PhotoRecognitionResult.Recognised { Recognition.Items.Count: > 0 } => Pass(),
                PhotoRecognitionResult.Recognised => new CheckResult(CheckIds.Photo, CheckStates.Warn, "It answered in the right shape but named nothing on the pack."),
                PhotoRecognitionResult.Unavailable u => Fail(ImageReason(Reason(u.Reason))),
                _ => Fail("No answer came back."),
            };
        });

        await run(CheckIds.GenericNames, async () =>
        {
            using var throttle = new FoundryThrottle(nameOptions with { MinInterval = TimeSpan.Zero }, clock);
            var writer = new FoundryGenericNameWriter(
                http, nameOptions with { Deployment = deployment, Attempts = 2 }, throttle, credential, clock,
                loggers.CreateLogger<FoundryGenericNameWriter>(), model);
            var product = new ProductDescription("Baked beans in tomato sauce", null, "400 g", []);
            return await writer.WriteAsync(product, "Tins and jars", ["Chopped tomatoes", "Sweetcorn"], cancellationToken) switch
            {
                GenericWriteResult.Written { Name.Length: > 0 } => Pass(),
                GenericWriteResult.Written => Fail("It answered with an empty name."),
                GenericWriteResult.Unavailable u => Fail(Reason(u.Reason)),
                GenericWriteResult.Failed f => Fail(Reason(f.Reason)),
                _ => Fail("Not configured."),
            };
        });
    }

    private static async Task<CheckResult> TemperatureAsync(
        FoundryProbe probe, string deployment, FixedActiveModels model, CancellationToken cancellationToken)
    {
        var usual = await probe.ChatAsync(deployment, "You answer in one word.", Ping, 0.7, null, CallLimit, cancellationToken);
        if (usual.Ok)
        {
            model.Mode = TemperatureMode.Usual;
            return Pass();
        }

        if (!IsTemperatureRefusal(usual.Call))
        {
            return Fail(Explain(usual.Call));
        }

        var plain = await probe.ChatAsync(deployment, "You answer in one word.", Ping, null, null, CallLimit, cancellationToken);
        if (!plain.Ok)
        {
            return Fail(Explain(plain.Call));
        }

        model.Mode = TemperatureMode.Default;
        return new CheckResult(CheckIds.Temperature, CheckStates.Warn, "This model needs its default temperature; we'll leave it out.");
    }

    private static async Task<CheckResult> StrictSchemaAsync(
        FoundryProbe probe, string deployment, FixedActiveModels model, CancellationToken cancellationToken)
    {
        var reply = await probe.ChatAsync(
            deployment, "Give a short generic shopping-list name for the product.", "Product: Baked beans in tomato sauce, 400 g",
            model.Mode == TemperatureMode.Default ? null : 0, FoundryGenericNameWriter.Schema, CallLimit, cancellationToken);
        if (!reply.Ok)
        {
            return Fail(Explain(reply.Call));
        }

        try
        {
            return JsonNode.Parse(reply.Content!) is JsonObject o && o["genericName"] is JsonValue v && v.TryGetValue<string>(out var name) && name.Length > 0
                ? Pass()
                : Fail("The answer was JSON, but not in the shape of the schema.");
        }
        catch (JsonException)
        {
            return Fail("The answer wasn't valid JSON.");
        }
    }

    /// <summary>Check 9: warn if any call took over 20 s, fail if any hit the 45 s limit.</summary>
    internal static CheckResult Speed(IReadOnlyList<AiCall> calls)
    {
        var slowest = calls.Count == 0 ? 0 : calls.Max(c => c.Ms);
        var timedOut = calls.Count(c => c.Outcome == AiCall.Timeout);
        if (timedOut > 0)
        {
            return new CheckResult(CheckIds.Speed, CheckStates.Fail,
                $"{timedOut} call{(timedOut == 1 ? "" : "s")} hit the {CallLimit.TotalSeconds:0} s limit.", slowest);
        }

        return slowest > SlowCall.TotalMilliseconds
            ? new CheckResult(CheckIds.Speed, CheckStates.Warn, $"The slowest call took {Seconds(slowest)}.", slowest)
            : new CheckResult(CheckIds.Speed, CheckStates.Pass, calls.Count == 0 ? "Nothing to time." : $"The slowest call took {Seconds(slowest)}.", slowest);
    }

    private sealed record CostFigures(RunCosts Costs, ModelPrice? Price);

    private async Task<CostFigures> CostsAsync(
        string deployment, IReadOnlyList<AiCall> calls, Dictionary<string, CheckResult> results, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        if (calls.Select(c => c.ModelVersion).FirstOrDefault(v => v is not null) is { } version)
        {
            names.Add(version);
        }

        names.Add(deployment);
        var price = await prices.LookupAsync(names, cancellationToken);
        if (price is null)
        {
            return new CostFigures(new RunCosts(null, null, null, null, null), null);
        }

        decimal? Cost(int? prompt, int? completion) => prompt is null || completion is null
            ? null
            : (prompt.Value * price.InputPerMillion + completion.Value * price.OutputPerMillion) / 1_000_000m;

        CheckResult Of(string id) => results[id];
        var all = Cost(results.Values.Sum(r => r.PromptTokens ?? 0), results.Values.Sum(r => r.CompletionTokens ?? 0));
        return new CostFigures(
            new RunCosts(
                Ran(Of(CheckIds.Recipe)) ? Cost(Of(CheckIds.Recipe).PromptTokens, Of(CheckIds.Recipe).CompletionTokens) : null,
                Ran(Of(CheckIds.ReadyNow)) ? Cost(Of(CheckIds.ReadyNow).PromptTokens, Of(CheckIds.ReadyNow).CompletionTokens) : null,
                Ran(Of(CheckIds.Photo)) ? Cost(Of(CheckIds.Photo).PromptTokens, Of(CheckIds.Photo).CompletionTokens) : null,
                all, price.Basis),
            price);
    }

    private static bool Ran(CheckResult result) => result.PromptTokens is > 0;

    /// <summary>Check 10: always passes, unless the output tokens are far above gpt-4o's.</summary>
    private static CheckResult CostCheck(CostFigures figures, Dictionary<string, CheckResult> results)
    {
        var recipeTokens = results[CheckIds.Recipe].CompletionTokens ?? 0;
        var ideaTokens = results[CheckIds.ReadyNow].CompletionTokens ?? 0;
        var heavy = recipeTokens > Gpt4oRecipeCompletionTokens * TokenWarnFactor || ideaTokens > Gpt4oReadyNowCompletionTokens * TokenWarnFactor;
        var how = figures.Costs.PerRecipe is null && figures.Costs.PerReadyNow is null && figures.Costs.PerPhoto is null
            ? (figures.Price is null ? "Estimated cost unknown: no list price matched." : "No cost to estimate.")
            : $"About {Pence(figures.Costs.PerRecipe)} a recipe, {Pence(figures.Costs.PerReadyNow)} a Ready now set, {Pence(figures.Costs.PerPhoto)} a photo (list price).";
        return heavy
            ? new CheckResult(CheckIds.Cost, CheckStates.Warn,
                $"{how} It writes far more than gpt-4o does ({recipeTokens} tokens for a recipe, against about {Gpt4oRecipeCompletionTokens}); hidden reasoning is billed as output.")
            : new CheckResult(CheckIds.Cost, CheckStates.Pass, how);
    }

    internal static string Pence(decimal? pounds) => pounds is null
        ? "unknown"
        : pounds.Value >= 1m ? $"£{pounds.Value.ToString("0.00", CultureInfo.InvariantCulture)}" : $"{(pounds.Value * 100).ToString("0.0", CultureInfo.InvariantCulture)}p";

    private static string Seconds(int ms) => $"{(ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} s";

    private static CheckResult Pass() => new("", CheckStates.Pass);

    private static CheckResult Fail(string reason) => new("", CheckStates.Fail, reason);

    /// <summary>Foundry's own code and message when a call was refused, else the caller's reason (timeout, unreadable…). Filled
    /// from the <see cref="AiCallLog"/> of the check running.</summary>
    private static string Reason(string callerReason)
    {
        var refused = AiCallLog.Current?.Calls.LastOrDefault(c => c.Code is not null);
        return refused is null ? Capitalise(callerReason) : Explain(refused);
    }

    private static string ImageReason(string reason) =>
        AiCallLog.Current?.Calls.LastOrDefault(c => c.Code is not null) is { } refused
        && ($"{refused.Code} {refused.Message}").Contains("image", StringComparison.OrdinalIgnoreCase)
            ? $"This model doesn't accept image input: {refused.Code}: {refused.Message}"
            : reason;

    /// <summary>One line for a call that didn't work.</summary>
    internal static string Explain(AiCall call) => call switch
    {
        { Status: 404, Code: "DeploymentNotFound" } => "No deployment with that name on this Foundry account.",
        { Outcome: AiCall.Timeout } => $"No answer within {CallLimit.TotalSeconds:0} s.",
        { Outcome: AiCall.Network } => "Couldn't reach Foundry.",
        { Outcome: AiCall.Unreadable } => "Foundry's answer couldn't be read.",
        { Outcome: AiCall.Filtered } => "Foundry's content filter stopped it.",
        { Code: not null } => $"Foundry refused it (HTTP {call.Status}): {call.Code}: {call.Message}",
        { Status: not null } => $"Foundry answered HTTP {call.Status}.",
        _ => "It didn't work.",
    };

    internal static bool IsTemperatureRefusal(AiCall call) =>
        call is { Status: 400 }
        && (call.Code is "unsupported_value" or "unsupported_parameter"
            || (call.Message ?? "").Contains("temperature", StringComparison.OrdinalIgnoreCase));

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A fixed model for the real callers: this deployment, in the mode the temperature check found.</summary>
    private sealed class FixedActiveModels(string deployment) : IActiveAiModels
    {
        public TemperatureMode Mode { get; set; } = TemperatureMode.Usual;

        public Task<ResolvedModel> ForAsync(AiJob job, string fallbackDeployment, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedModel(deployment, Mode));

        public void Invalidate()
        {
        }
    }
}
