using System.Globalization;
using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Recipes;

/// <summary>Recipe ideas from what's in stock (#73). Needs a signed-in user. See docs/api.md.</summary>
public sealed class RecipeFunctions(SuggestRecipe suggest)
{
    private const int MaxBodyBytes = 16 * 1024;

    /// <summary>The message the app shows when the model couldn't help (a 200, so the error alert isn't fired).</summary>
    public const string UnavailableMessage = "Couldn't come up with a recipe just now. Try again.";

    public const string TooManyMessage = "That's a lot of recipes for one hour. Try again a bit later.";

    /// <summary><c>POST /api/recipes/suggest</c> with <c>{ meal, preferUseFirst, includeProductIds, comments,
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
                    _ => null,
                }
                : null;
            if (meal is null)
            {
                return (null, "meal must be \"breakfast\", \"lunch\" or \"dinner\".");
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

            return (new RecipeChoices(meal.Value, prefer, include, comments, avoid), null);
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(SuggestRecipeResult result) => result switch
    {
        SuggestRecipeResult.Suggested s => (HttpStatusCode.OK, new { available = true, recipe = ToJson(s.Recipe) }),
        SuggestRecipeResult.Unavailable => (HttpStatusCode.OK, new { available = false, message = UnavailableMessage }),
        SuggestRecipeResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        SuggestRecipeResult.NothingInStock => (HttpStatusCode.UnprocessableEntity, new { error = "There's nothing in stock to cook with." }),
        SuggestRecipeResult.TooManyRequests => ((HttpStatusCode)429, new { error = TooManyMessage }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
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
