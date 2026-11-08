using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Ratings;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Recipes;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Ratings;

/// <summary>Ratings (#128): 👍 / 👎 on recipes, Ready now ideas and planned meals, kept per person, and the household's
/// ratings grouped by dish for Our taste. All need a signed-in user; you change or remove only your own. See docs/api.md.</summary>
public sealed class RatingFunctions(RatingUseCases ratings)
{
    private const int MaxBodyBytes = 16 * 1024;

    /// <summary><c>GET /api/ratings</c> → <c>{ total, dishes: [...] }</c>, the dish with the newest rating first.</summary>
    [Function("ratings")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ratings")] HttpRequestData request,
        FunctionContext context, CancellationToken cancellationToken)
    {
        var dishes = await ratings.ListAsync(context.GetCurrentUser(), cancellationToken);
        return await JsonAsync(request, HttpStatusCode.OK, new
        {
            total = dishes.Sum(d => d.Ratings.Count),
            dishes = dishes.Select(d => new
            {
                kind = d.Kind,
                key = d.NormalizedTitle,
                title = d.Snapshot.Title,
                meal = d.Snapshot.Meal.ToString().ToLowerInvariant(),
                diet = Diets.Id(d.Snapshot.Diet),
                totalMinutes = d.Snapshot.TotalMinutes,
                latestAt = d.LatestAt,
                ratings = d.Ratings.Select(r => ToJson(r.Rating, r.Mine)),
            }),
        });
    }

    /// <summary><c>PUT /api/ratings/{id}</c>: rate a dish. Idempotent by id; rating the same dish again replaces your earlier rating.</summary>
    [Function("putRating")]
    public async Task<HttpResponseData> Put(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "ratings/{id:guid}")] HttpRequestData request,
        Guid id, FunctionContext context, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadAsync(request, cancellationToken);
        if (body is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (put, problem) = ParsePut(id, body);
        body.Dispose();
        return put is null
            ? await JsonAsync(request, HttpStatusCode.BadRequest, new { error = problem })
            : await RespondAsync(request, await ratings.PutAsync(put, context.GetCurrentUser(), cancellationToken));
    }

    /// <summary><c>PATCH /api/ratings/{id}</c> with <c>{ reasons, dislikedIngredients? }</c>: change the reasons of your own 👎.</summary>
    [Function("updateRating")]
    public async Task<HttpResponseData> Patch(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "ratings/{id:guid}")] HttpRequestData request,
        Guid id, FunctionContext context, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadAsync(request, cancellationToken);
        if (body is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        using var document = body;
        var root = document.RootElement;
        if (Strings(root, "reasons") is not { } reasons || Strings(root, "dislikedIngredients") is not { } disliked)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Body must be { reasons: [...], dislikedIngredients?: [...] }." });
        }

        return await RespondAsync(request, await ratings.UpdateReasonsAsync(id, reasons, disliked, context.GetCurrentUser(), cancellationToken));
    }

    /// <summary><c>DELETE /api/ratings/{id}</c>: take back your own rating. Idempotent (204 whether or not it was there); 403 for someone else's.</summary>
    [Function("deleteRating")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "ratings/{id:guid}")] HttpRequestData request,
        Guid id, FunctionContext context, CancellationToken cancellationToken) =>
        await ratings.DeleteAsync(id, context.GetCurrentUser(), cancellationToken) switch
        {
            RatingResult.Forbidden => await JsonAsync(request, HttpStatusCode.Forbidden, new { error = "That's someone else's rating." }),
            _ => request.CreateResponse(HttpStatusCode.NoContent),
        };

    internal static (PutRatingRequest? Request, string? Error) ParsePut(Guid id, JsonDocument document)
    {
        var root = document.RootElement;
        string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Number(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        Guid? Id(string name) => Guid.TryParse(Text(name), out var g) ? g : null;

        RecipeMeal? meal = Text("meal") switch
        {
            "breakfast" => RecipeMeal.Breakfast,
            "lunch" => RecipeMeal.Lunch,
            "dinner" => RecipeMeal.Dinner,
            "snack" => RecipeMeal.Snack,
            _ => null,
        };
        if (Text("rating") is not { } value || Text("context") is not { } context || Text("kind") is not { } kind
            || Text("title") is not { } title || meal is null || Number("totalMinutes") is not { } minutes
            || Strings(root, "reasons") is not { } reasons || Strings(root, "dislikedIngredients") is not { } disliked
            || Strings(root, "equipment") is not { } equipment || !TryIngredients(root, out var ingredients))
        {
            return (null, "Body must be { rating, context, kind, title, meal, totalMinutes, ingredients: [{ name, genericName? }], reasons?: [...], dislikedIngredients?: [...], equipment?: [...], diet?, savedRecipeId?, planId?, mealId? }.");
        }

        var (diet, dietError) = RecipeFunctions.ParseDiet(root);
        if (dietError is not null)
        {
            return (null, dietError);
        }

        return (new PutRatingRequest(id, value, reasons, disliked, context,
            new RatingSnapshot(kind, title, meal.Value, diet, minutes, ingredients, equipment, Id("savedRecipeId"), Id("planId"), Text("mealId"))), null);
    }

    /// <summary>An optional list of strings: missing is empty; anything else that isn't a list of strings is null.</summary>
    private static List<string>? Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
        {
            return null;
        }

        return [.. value.EnumerateArray().Select(x => x.GetString()!)];
    }

    private static bool TryIngredients(JsonElement root, out List<RatingIngredient> ingredients)
    {
        ingredients = [];
        if (!root.TryGetProperty("ingredients", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return root.TryGetProperty("ingredients", out var absent) is false; // optional
        }

        foreach (var i in list.EnumerateArray())
        {
            if (i.ValueKind != JsonValueKind.Object || !i.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            ingredients.Add(new RatingIngredient(
                name.GetString()!, i.TryGetProperty("genericName", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null));
        }

        return true;
    }

    internal static object ToJson(Rating r, bool mine) => new
    {
        id = r.Id,
        rating = r.Value,
        reasons = r.Reasons,
        dislikedIngredients = r.DislikedIngredients,
        context = r.Context,
        ratedAt = r.RatedAt,
        updatedAt = r.UpdatedAt,
        personName = r.PersonName,
        mine,
        kind = r.Snapshot.Kind,
        title = r.Snapshot.Title,
        key = r.NormalizedTitle,
        meal = r.Snapshot.Meal.ToString().ToLowerInvariant(),
        diet = Diets.Id(r.Snapshot.Diet),
        totalMinutes = r.Snapshot.TotalMinutes,
        ingredients = r.Snapshot.Ingredients.Select(i => new { name = i.Name, genericName = i.GenericName }),
        equipment = r.Snapshot.Equipment,
        savedRecipeId = r.Snapshot.SavedRecipeId,
        planId = r.Snapshot.PlanId,
        mealId = r.Snapshot.MealId,
    };

    private static async Task<HttpResponseData> RespondAsync(HttpRequestData request, RatingResult result) => result switch
    {
        RatingResult.Saved s => await JsonAsync(request, HttpStatusCode.OK, ToJson(s.Rating, mine: true)),
        RatingResult.Invalid i => await JsonAsync(request, HttpStatusCode.BadRequest, new { error = i.Error }),
        RatingResult.NotFound => await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such rating." }),
        RatingResult.Forbidden => await JsonAsync(request, HttpStatusCode.Forbidden, new { error = "That's someone else's rating." }),
        RatingResult.Conflict => await JsonAsync(request, HttpStatusCode.Conflict, new { error = "It changed meanwhile. Try again." }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static async Task<(JsonDocument? Body, string? Error)> ReadAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaxBodyBytes)
        {
            return (null, "Body is too large.");
        }

        try
        {
            var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return (document, null);
            }

            document.Dispose();
            return (null, "Body must be a JSON object.");
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

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
