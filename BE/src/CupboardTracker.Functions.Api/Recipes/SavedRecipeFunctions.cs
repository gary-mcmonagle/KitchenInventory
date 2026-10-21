using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Recipes;
using CupboardTracker.Application.SavedRecipes;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Recipes;

/// <summary>Saved recipes and Ready now ideas (#105): one household collection, each checked against today's stock.
/// All need a signed-in user. See docs/api.md.</summary>
public sealed class SavedRecipeFunctions(SavedRecipeUseCases saved)
{
    private const int MaxBodyBytes = 32 * 1024;

    /// <summary><c>GET /api/saved-recipes</c> → <c>{ savedRecipes: [...] }</c>, newest first.</summary>
    [Function("savedRecipes")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "saved-recipes")] HttpRequestData request, CancellationToken cancellationToken) =>
        await JsonAsync(request, HttpStatusCode.OK, new { savedRecipes = (await saved.ListAsync(cancellationToken)).Select(ToJson) });

    /// <summary><c>GET /api/saved-recipes/{id}</c>.</summary>
    [Function("savedRecipe")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "saved-recipes/{id:guid}")] HttpRequestData request,
        Guid id, CancellationToken cancellationToken) =>
        await saved.GetAsync(id, cancellationToken) is { } view
            ? await JsonAsync(request, HttpStatusCode.OK, ToJson(view))
            : await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such saved recipe." });

    /// <summary><c>PUT /api/saved-recipes/{id}</c>: save a snapshot. Idempotent by id.</summary>
    [Function("saveRecipe")]
    public async Task<HttpResponseData> Save(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "saved-recipes/{id:guid}")] HttpRequestData request,
        Guid id, FunctionContext context, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadAsync(request, cancellationToken);
        if (body is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (save, problem) = ParseSave(id, body);
        body.Dispose();
        return save is null
            ? await JsonAsync(request, HttpStatusCode.BadRequest, new { error = problem })
            : await RespondAsync(request, await saved.SaveAsync(save, context.GetCurrentUser(), cancellationToken));
    }

    /// <summary><c>PATCH /api/saved-recipes/{id}</c> with <c>{ title?, note? }</c>.</summary>
    [Function("updateSavedRecipe")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "saved-recipes/{id:guid}")] HttpRequestData request,
        Guid id, FunctionContext context, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadAsync(request, cancellationToken);
        if (body is null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        using var document = body;
        var root = document.RootElement;
        string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return await RespondAsync(request, await saved.UpdateAsync(id, Text("title"), Text("note"), context.GetCurrentUser(), cancellationToken));
    }

    /// <summary><c>DELETE /api/saved-recipes/{id}</c>. Undo is a PUT of the snapshot.</summary>
    [Function("deleteSavedRecipe")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "saved-recipes/{id:guid}")] HttpRequestData request,
        Guid id, CancellationToken cancellationToken) =>
        await saved.DeleteAsync(id, cancellationToken)
            ? request.CreateResponse(HttpStatusCode.NoContent)
            : await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such saved recipe." });

    internal static (SaveRecipeRequest? Request, string? Error) ParseSave(Guid id, JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, "Body must be a JSON object.");
        }

        string? Text(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Number(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        List<string>? Strings(string name)
        {
            if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                return [];
            }

            return v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
                ? [.. v.EnumerateArray().Select(e => e.GetString()!)]
                : null;
        }

        var kind = Text(root, "kind");
        RecipeMeal? meal = Text(root, "meal") switch
        {
            "breakfast" => RecipeMeal.Breakfast,
            "lunch" => RecipeMeal.Lunch,
            "dinner" => RecipeMeal.Dinner,
            "snack" => RecipeMeal.Snack,
            _ => null,
        };
        var listName = kind == SavedRecipeKinds.ReadyNow ? "uses" : "ingredients";
        if (Text(root, "title") is not { } title || meal is null || Number("totalMinutes") is not { } minutes
            || !root.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array
            || Strings("steps") is not { } steps || Strings("alsoNeeded") is not { } also)
        {
            return (null, $"Body must be {{ kind, title, meal, totalMinutes, {listName}: [{{ name, amount?, productId?, genericName? }}], steps?, alsoNeeded?, how?, appliance?, note? }}.");
        }

        var ingredients = new List<SavedIngredient>();
        foreach (var i in list.EnumerateArray())
        {
            if (i.ValueKind != JsonValueKind.Object || Text(i, "name") is not { } name)
            {
                return (null, $"Every item in {listName} needs a name.");
            }

            ingredients.Add(new SavedIngredient(name, Text(i, "amount"),
                Guid.TryParse(Text(i, "productId"), out var p) ? p : null, Text(i, "genericName")));
        }

        return (new SaveRecipeRequest(id, kind ?? "", title, meal.Value, minutes, Number("servings"), ingredients, steps, also,
            Text(root, "how"), Text(root, "appliance"), Text(root, "note"), Text(root, "originalTitle")), null);
    }

    internal static object ToJson(SavedRecipeView view)
    {
        var r = view.Recipe;
        var items = r.Ingredients.Zip(view.Statuses, (i, s) => new
        {
            name = i.Name,
            amount = i.Amount,
            productId = i.ProductId,
            genericName = i.GenericName,
            status = s switch { IngredientStatus.Have => "have", IngredientStatus.UseFirst => "useFirst", _ => "missing" },
        });
        return new
        {
            id = r.Id,
            kind = r.Kind,
            title = r.Title,
            originalTitle = r.OriginalTitle,
            meal = r.Meal.ToString().ToLowerInvariant(),
            totalMinutes = r.TotalMinutes,
            servings = r.Servings,
            ingredients = items,
            steps = r.Steps,
            alsoNeeded = r.AlsoNeeded,
            how = r.How,
            appliance = r.Appliance,
            note = r.Note,
            savedByName = r.SavedByName,
            savedAt = r.SavedAt,
            updatedAt = r.UpdatedAt,
            missing = view.Missing,
            usesUp = view.UsesUp,
        };
    }

    private static async Task<HttpResponseData> RespondAsync(HttpRequestData request, SavedRecipeResult result) => result switch
    {
        SavedRecipeResult.Saved s => await JsonAsync(request, HttpStatusCode.OK, ToJson(s.View)),
        SavedRecipeResult.Invalid i => await JsonAsync(request, HttpStatusCode.BadRequest, new { error = i.Error }),
        SavedRecipeResult.NotFound => await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such saved recipe." }),
        SavedRecipeResult.Conflict => await JsonAsync(request, HttpStatusCode.Conflict, new { error = "It changed meanwhile. Try again." }),
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
