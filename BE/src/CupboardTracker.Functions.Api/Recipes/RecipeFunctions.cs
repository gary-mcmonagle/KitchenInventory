using System.Globalization;
using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Recipes;

/// <summary>Hungry? (#73, #88): a recipe or Ready now ideas from what's in stock. Needs a signed-in user. See docs/api.md.</summary>
public sealed class RecipeFunctions(SuggestRecipe suggest, AdjustRecipe adjust, SuggestRecipeAdditions additions)
{
    private const int MaxBodyBytes = 16 * 1024;

    /// <summary>The message the app shows when the model couldn't help (a 200, so the error alert isn't fired).</summary>
    public const string UnavailableMessage = "Couldn't come up with a recipe just now. Try again.";

    public const string TooManyMessage = "That's a lot of recipes for one hour. Try again a bit later.";

    /// <summary><c>POST /api/recipes/suggest</c> with <c>{ mode, meal, preferUseFirst, includeProductIds, comments,
    /// avoidTitles }</c>. Nothing is stored.</summary>
    [Function("suggestRecipe")]
    public async Task<HttpResponseData> Suggest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "recipes/suggest")] HttpRequestData request,
        FunctionContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (choices, error) = Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (choices is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (status, body) = ToResponse(await suggest.ExecuteAsync(choices, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>POST /api/recipes/adjust</c> (#104) with <c>{ recipe, add: { productIds, typed }, remove, tweak }</c>:
    /// the same recipe, updated. Counts towards the hourly limit. Nothing is stored.</summary>
    [Function("adjustRecipe")]
    public async Task<HttpResponseData> Adjust(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "recipes/adjust")] HttpRequestData request,
        FunctionContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (choices, error) = ParseAdjust(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (choices is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (status, body) = ToResponse(await adjust.ExecuteAsync(choices, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>POST /api/recipes/suggest-additions</c> (#104) with <c>{ recipe }</c> → <c>{ suggestions: [{ name,
    /// productIds, useFirst, confidence }] }</c> (0 to 3). Not rate limited (Jev only, cheap). Nothing is stored.</summary>
    [Function("suggestRecipeAdditions")]
    public async Task<HttpResponseData> SuggestAdditions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "recipes/suggest-additions")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (recipe, error) = ParseRecipeOnly(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (recipe is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var suggestions = await additions.ExecuteAsync(recipe, cancellationToken);
        return await JsonAsync(request, HttpStatusCode.OK, new
        {
            suggestions = suggestions.Select(s => new { name = s.Name, productIds = s.ProductIds, useFirst = s.UseFirst, confidence = s.Confidence }),
        });
    }

    internal static (Recipe? Recipe, string? Error) ParseRecipeOnly(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
        {
            return (null, "Body is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("recipe", out var r)
                ? ParseRecipe(r)
                : (null, "Body must be { recipe }.");
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    internal static (AdjustChoices? Choices, string? Error) ParseAdjust(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
        {
            return (null, "Body is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("recipe", out var r))
            {
                return (null, "Body must be { recipe, add, remove, tweak }.");
            }

            var (recipe, error) = ParseRecipe(r);
            if (recipe is null)
            {
                return (null, error);
            }

            var changes = ParseChanges(root);
            return changes.Error is null
                ? (new AdjustChoices(recipe, changes.AddIds!, changes.Typed!, changes.Remove!, changes.Tweak), null)
                : (null, changes.Error);
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    /// <summary>An adjustment's changes (#104, #108): <c>add: { productIds, typed }</c>, <c>remove</c> and <c>tweak</c>.</summary>
    internal static (List<Guid>? AddIds, List<string>? Typed, List<string>? Remove, string? Tweak, string? Error) ParseChanges(JsonElement root)
    {
        var addIds = new List<Guid>();
        var typed = new List<string>();
        if (root.TryGetProperty("add", out var add) && add.ValueKind != JsonValueKind.Null)
        {
            if (add.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null, null, "add must be { productIds, typed }.");
            }

            if (add.TryGetProperty("productIds", out var ids) && ids.ValueKind != JsonValueKind.Null)
            {
                if (ids.ValueKind != JsonValueKind.Array
                    || ids.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String || !Guid.TryParse(i.GetString(), out _)))
                {
                    return (null, null, null, null, "add.productIds must be a list of product IDs.");
                }

                addIds.AddRange(ids.EnumerateArray().Select(i => Guid.Parse(i.GetString()!)));
            }

            if (Strings(add, "typed") is not { } t)
            {
                return (null, null, null, null, "add.typed must be a list of strings.");
            }

            typed.AddRange(t);
        }

        if (Strings(root, "remove") is not { } remove)
        {
            return (null, null, null, null, "remove must be a list of ingredient names.");
        }

        string? tweak = null;
        if (root.TryGetProperty("tweak", out var tw) && tw.ValueKind != JsonValueKind.Null)
        {
            if (tw.ValueKind != JsonValueKind.String)
            {
                return (null, null, null, null, "tweak must be a string or null.");
            }

            tweak = tw.GetString();
        }

        return (addIds, typed, remove, tweak, null);
    }

    /// <summary>A list of strings, an empty list when it's missing or null, or null when it isn't one.</summary>
    private static List<string>? Strings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String)
            ? [.. value.EnumerateArray().Select(v => v.GetString()!)]
            : null;
    }

    /// <summary>The recipe as the app last showed it: only the starting point for an adjustment, so it's checked for
    /// shape and size, not trusted (the stock is rebuilt from Cosmos).</summary>
    internal static (Recipe? Recipe, string? Error) ParseRecipe(JsonElement r)
    {
        const string Shape = "recipe must be { title, summary, meal, servings, totalMinutes, ingredients: [{ name, amount, productId }], steps, alsoNeeded }.";
        if (r.ValueKind != JsonValueKind.Object)
        {
            return (null, Shape);
        }

        static string? Text(JsonElement o, string name, int max) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } t && t.Length <= max ? t : null;
        static int? Number(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

        var title = Text(r, "title", 120);
        RecipeMeal? meal = Text(r, "meal", 20) switch
        {
            "breakfast" => RecipeMeal.Breakfast,
            "lunch" => RecipeMeal.Lunch,
            "dinner" => RecipeMeal.Dinner,
            "snack" => RecipeMeal.Snack,
            _ => null,
        };
        if (title is null || meal is null || !r.TryGetProperty("ingredients", out var list) || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() is 0 or > 40 || Strings(r, "steps") is not { Count: <= 20 } steps || Strings(r, "alsoNeeded") is not { Count: <= 5 } also)
        {
            return (null, Shape);
        }

        var ingredients = new List<RecipeIngredient>();
        foreach (var i in list.EnumerateArray())
        {
            var name = i.ValueKind == JsonValueKind.Object ? Text(i, "name", 100) : null;
            if (name is null)
            {
                return (null, Shape);
            }

            Guid? productId = i.TryGetProperty("productId", out var p) && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out var g) ? g : null;
            ingredients.Add(new RecipeIngredient(name, Text(i, "amount", 60) ?? "", productId));
        }

        static string Cap(string s, int max) => s.Length > max ? s[..max] : s;
        return (new Recipe(
            title,
            Cap(Text(r, "summary", 1000) ?? "", 300),
            meal.Value,
            Math.Clamp(Number(r, "servings") ?? 2, 1, 12),
            Math.Clamp(Number(r, "totalMinutes") ?? 30, 1, 600),
            ingredients,
            [.. steps.Select(s => Cap(s.Trim(), 400)).Where(s => s.Length > 0)],
            [],
            [.. also.Select(a => Cap(a.Trim(), 60)).Where(a => a.Length > 0)],
            []), null);
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(AdjustRecipeResult result) => result switch
    {
        AdjustRecipeResult.Adjusted a => (HttpStatusCode.OK, new
        {
            available = true,
            recipe = ToJson(a.Recipe),
            adjustmentSummary = new { added = a.Summary.Added, removed = a.Summary.Removed, tweak = a.Summary.Tweak },
        }),
        AdjustRecipeResult.Unavailable => (HttpStatusCode.OK, new { available = false, message = AdjustUnavailableMessage }),
        AdjustRecipeResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        AdjustRecipeResult.NothingInStock => (HttpStatusCode.UnprocessableEntity, new { error = "There's nothing in stock to cook with." }),
        AdjustRecipeResult.TooManyRequests => ((HttpStatusCode)429, new { error = TooManyMessage }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    public const string AdjustUnavailableMessage = "Couldn't update the recipe just now. Try again.";

    internal static (RecipeChoices? Choices, string? Error) Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
        {
            return (null, "Body is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "Body must be a JSON object.");
            }

            RecipeMeal? meal = root.TryGetProperty("meal", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() switch
                {
                    "breakfast" => RecipeMeal.Breakfast,
                    "lunch" => RecipeMeal.Lunch,
                    "dinner" => RecipeMeal.Dinner,
                    "snack" => RecipeMeal.Snack,
                    _ => null,
                }
                : null;
            if (meal is null)
            {
                return (null, "meal must be \"breakfast\", \"lunch\", \"dinner\" or \"snack\".");
            }

            var mode = RecipeMode.Cook;
            if (root.TryGetProperty("mode", out var md) && md.ValueKind != JsonValueKind.Null)
            {
                RecipeMode? parsed = md.ValueKind == JsonValueKind.String
                    ? md.GetString() switch { "cook" => RecipeMode.Cook, "readyNow" => RecipeMode.ReadyNow, _ => null }
                    : null;
                if (parsed is null)
                {
                    return (null, "mode must be \"cook\" or \"readyNow\".");
                }

                mode = parsed.Value;
            }

            var prefer = false;
            if (root.TryGetProperty("preferUseFirst", out var p))
            {
                if (p.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return (null, "preferUseFirst must be true or false.");
                }

                prefer = p.GetBoolean();
            }

            var include = new List<Guid>();
            if (root.TryGetProperty("includeProductIds", out var ids) && ids.ValueKind != JsonValueKind.Null)
            {
                if (ids.ValueKind != JsonValueKind.Array)
                {
                    return (null, "includeProductIds must be a list of product IDs.");
                }

                foreach (var id in ids.EnumerateArray())
                {
                    if (id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var guid))
                    {
                        return (null, "includeProductIds must be a list of product IDs.");
                    }

                    include.Add(guid);
                }
            }

            string? comments = null;
            if (root.TryGetProperty("comments", out var c) && c.ValueKind != JsonValueKind.Null)
            {
                if (c.ValueKind != JsonValueKind.String)
                {
                    return (null, "comments must be a string or null.");
                }

                comments = c.GetString();
            }

            var avoid = new List<string>();
            if (root.TryGetProperty("avoidTitles", out var titles) && titles.ValueKind != JsonValueKind.Null)
            {
                if (titles.ValueKind != JsonValueKind.Array || titles.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String))
                {
                    return (null, "avoidTitles must be a list of strings.");
                }

                avoid.AddRange(titles.EnumerateArray().Select(t => t.GetString()!));
            }

            return (new RecipeChoices(meal.Value, prefer, include, comments, avoid, mode), null);
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(SuggestRecipeResult result) => result switch
    {
        SuggestRecipeResult.Suggested s => (HttpStatusCode.OK, new { available = true, mode = "cook", recipe = ToJson(s.Recipe) }),
        SuggestRecipeResult.IdeasSuggested i => (HttpStatusCode.OK, new
        {
            available = true,
            mode = "readyNow",
            meal = i.Meal.ToString().ToLowerInvariant(),
            ideas = i.Ideas.Select(ToJson),
        }),
        SuggestRecipeResult.Unavailable => (HttpStatusCode.OK, new { available = false, message = UnavailableMessage }),
        SuggestRecipeResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        SuggestRecipeResult.NothingInStock => (HttpStatusCode.UnprocessableEntity, new { error = "There's nothing in stock to cook with." }),
        SuggestRecipeResult.TooManyRequests => ((HttpStatusCode)429, new { error = TooManyMessage }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static object ToJson(QuickIdeaView i) => new
    {
        title = i.Title,
        how = i.How,
        appliance = i.Appliance.ToString().ToLowerInvariant(),
        minutes = i.Minutes,
        uses = i.Uses.Select(u => new
        {
            productId = u.ProductId,
            displayName = u.DisplayName,
            useFirst = u.UseFirst,
            daysUntilSoonest = u.DaysUntilSoonest,
            soonestEstimatedExpiryDate = u.SoonestEstimatedExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }),
        alsoNeeded = i.AlsoNeeded,
    };

    private static object ToJson(RecipeView r) => new
    {
        title = r.Title,
        summary = r.Summary,
        meal = r.Meal.ToString().ToLowerInvariant(),
        servings = r.Servings,
        totalMinutes = r.TotalMinutes,
        ingredients = r.Ingredients.Select(i => new { name = i.Name, amount = i.Amount, productId = i.ProductId, useFirst = i.UseFirst }),
        steps = r.Steps,
        usesUp = r.UsesUp.Select(u => new
        {
            productId = u.ProductId,
            displayName = u.DisplayName,
            daysUntilSoonest = u.DaysUntilSoonest,
            soonestEstimatedExpiryDate = u.SoonestEstimatedExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }),
        alsoNeeded = r.AlsoNeeded,
        couldNotFitIn = r.CouldNotFitIn,
        couldNotAdd = r.CouldNotAdd ?? [],
        couldNotRemove = r.CouldNotRemove ?? [],
    };

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData request, HttpStatusCode status, object body)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, WebJson));
        return response;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}

/// <summary><c>POST /api/recipes/adjust-idea</c> (#108) with <c>{ idea: { title, how, appliance, minutes, uses, alsoNeeded },
/// meal, add: { productIds, typed }, remove, tweak }</c>: the same Ready now idea, updated. Counts towards the hourly
/// recipe limit. Nothing is stored. See docs/api.md.</summary>
public sealed class IdeaAdjustFunctions(AdjustIdea adjust)
{
    private const int MaxBodyBytes = 16 * 1024;

    public const string UnavailableMessage = "Couldn't update this idea just now. Try again.";

    [Function("adjustIdea")]
    public async Task<HttpResponseData> Adjust(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "recipes/adjust-idea")] HttpRequestData request,
        FunctionContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (choices, error) = Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        var (status, body) = choices is null
            ? (HttpStatusCode.BadRequest, new { error })
            : ToResponse(await adjust.ExecuteAsync(choices, context.GetCurrentUser(), cancellationToken));
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, WebJson));
        return response;
    }

    private const string Shape = "Body must be { idea: { title, how, appliance, minutes, uses, alsoNeeded }, meal, add, remove, tweak }.";

    internal static (IdeaAdjustChoices? Choices, string? Error) Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
        {
            return (null, "Body is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("idea", out var i) || i.ValueKind != JsonValueKind.Object)
            {
                return (null, Shape);
            }

            static string? Text(JsonElement o, string name, int max) =>
                o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } t && t.Length <= max ? t : null;

            RecipeMeal? meal = Text(root, "meal", 20) switch
            {
                "breakfast" => RecipeMeal.Breakfast,
                "lunch" => RecipeMeal.Lunch,
                "dinner" => RecipeMeal.Dinner,
                "snack" => RecipeMeal.Snack,
                _ => null,
            };
            Appliance? appliance = Text(i, "appliance", 20) switch
            {
                "toaster" => Appliance.Toaster,
                "microwave" => Appliance.Microwave,
                "kettle" => Appliance.Kettle,
                "none" => Appliance.None,
                _ => null,
            };
            var title = Text(i, "title", 120);
            var how = Text(i, "how", 600);
            if (meal is null || appliance is null || title is null || how is null || !i.TryGetProperty("uses", out var usesNode)
                || usesNode.ValueKind != JsonValueKind.Array || usesNode.GetArrayLength() > 20)
            {
                return (null, Shape);
            }

            // Each use is a product ID, or { productId, displayName } as the app shows it.
            var uses = new List<IdeaUse>();
            foreach (var u in usesNode.EnumerateArray())
            {
                var id = u.ValueKind == JsonValueKind.String ? u.GetString() : u.ValueKind == JsonValueKind.Object ? Text(u, "productId", 64) : null;
                if (!Guid.TryParse(id, out var productId))
                {
                    return (null, "idea.uses must be product IDs.");
                }

                uses.Add(new IdeaUse(productId, u.ValueKind == JsonValueKind.Object ? Text(u, "displayName", 120) : null));
            }

            var minutes = i.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var n) ? n : 5;
            var current = new IdeaInput(title, how, appliance.Value, Math.Clamp(minutes, 1, 60), uses, Text(i, "alsoNeeded", 60));
            var changes = RecipeFunctions.ParseChanges(root);
            return changes.Error is null
                ? (new IdeaAdjustChoices(current, meal.Value, changes.AddIds!, changes.Typed!, changes.Remove!, changes.Tweak), null)
                : (null, changes.Error);
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(AdjustIdeaResult result) => result switch
    {
        AdjustIdeaResult.Adjusted a => (HttpStatusCode.OK, new
        {
            available = true,
            idea = new
            {
                title = a.Idea.Title,
                how = a.Idea.How,
                appliance = a.Idea.Appliance.ToString().ToLowerInvariant(),
                minutes = a.Idea.Minutes,
                uses = a.Idea.Uses.Select(u => new
                {
                    productId = u.ProductId,
                    displayName = u.DisplayName,
                    useFirst = u.UseFirst,
                    daysUntilSoonest = u.DaysUntilSoonest,
                    soonestEstimatedExpiryDate = u.SoonestEstimatedExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                }),
                alsoNeeded = a.Idea.AlsoNeeded,
                couldNotAdd = a.CouldNotAdd,
                couldNotRemove = a.CouldNotRemove,
            },
            adjustmentSummary = new { added = a.Summary.Added, removed = a.Summary.Removed, tweak = a.Summary.Tweak },
        }),
        AdjustIdeaResult.Unavailable => (HttpStatusCode.OK, new { available = false, message = UnavailableMessage }),
        AdjustIdeaResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        AdjustIdeaResult.NothingInStock => (HttpStatusCode.UnprocessableEntity, new { error = "There's nothing in stock to cook with." }),
        AdjustIdeaResult.TooManyRequests => ((HttpStatusCode)429, new { error = RecipeFunctions.TooManyMessage }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}
