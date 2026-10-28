using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CupboardTracker.Application.Plans;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Infrastructure.Plans;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Functions.Api.Plans;

/// <summary>Plan meals (#119). All need a signed-in user. A new plan is written in the background (202, then poll the plan);
/// every change is saved at once, and a failed call leaves the plan as it was. See docs/api.md.</summary>
public sealed class MealPlanFunctions(MealPlans plans)
{
    public const int MaxBodyBytes = 32 * 1024;

    /// <summary><c>POST /api/meal-plans</c>: queues a plan. 202 with the plan (<c>status: "generating"</c>).</summary>
    [Function("createMealPlan")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "meal-plans")] HttpRequestData request,
        FunctionContext context, CancellationToken cancellationToken)
    {
        var (choices, error) = ParseChoices(await ReadAsync(request, cancellationToken));
        return choices is null
            ? await JsonAsync(request, HttpStatusCode.BadRequest, new { error })
            : await RespondAsync(request, await plans.CreateAsync(choices, context.GetCurrentUser(), cancellationToken), created: true);
    }

    /// <summary><c>GET /api/meal-plans/{id}</c>: the plan so far (the app polls it while it's being made).</summary>
    [Function("getMealPlan")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "meal-plans/{id:guid}")] HttpRequestData request,
        Guid id, CancellationToken cancellationToken) =>
        await plans.GetAsync(id, cancellationToken) is { } view
            ? await JsonAsync(request, HttpStatusCode.OK, ToJson(view))
            : await JsonAsync(request, HttpStatusCode.NotFound, new { error = "No such plan." });

    /// <summary><c>DELETE /api/meal-plans/{id}</c>: idempotent, 204 even if it was already gone.</summary>
    [Function("deleteMealPlan")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "meal-plans/{id:guid}")] HttpRequestData request,
        Guid id, CancellationToken cancellationToken)
    {
        await plans.DeletePlanAsync(id, cancellationToken);
        return request.CreateResponse(HttpStatusCode.NoContent);
    }

    /// <summary><c>POST /api/meal-plans/{id}/adjust</c> with <c>{ comments }</c>: the whole plan, keeping kept meals.</summary>
    [Function("adjustMealPlan")]
    public async Task<HttpResponseData> AdjustPlan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "meal-plans/{id:guid}/adjust")] HttpRequestData request,
        Guid id, FunctionContext context, CancellationToken cancellationToken) =>
        await RespondAsync(request, await plans.AdjustPlanAsync(id, Comments(await ReadAsync(request, cancellationToken)), context.GetCurrentUser(), cancellationToken));

    /// <summary><c>POST /api/meal-plans/{id}/meals/{mealId}/adjust</c> with <c>{ comments }</c>.</summary>
    [Function("adjustPlannedMeal")]
    public async Task<HttpResponseData> AdjustMeal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "meal-plans/{id:guid}/meals/{mealId}/adjust")] HttpRequestData request,
        Guid id, string mealId, FunctionContext context, CancellationToken cancellationToken) =>
        await RespondAsync(request, await plans.AdjustMealAsync(id, mealId, Comments(await ReadAsync(request, cancellationToken)), context.GetCurrentUser(), cancellationToken));

    /// <summary><c>POST /api/meal-plans/{id}/meals/{mealId}/another</c>: a different meal of the same type.</summary>
    [Function("anotherPlannedMeal")]
    public async Task<HttpResponseData> Another(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "meal-plans/{id:guid}/meals/{mealId}/another")] HttpRequestData request,
        Guid id, string mealId, FunctionContext context, CancellationToken cancellationToken) =>
        await RespondAsync(request, await plans.AnotherAsync(id, mealId, context.GetCurrentUser(), cancellationToken));

    /// <summary><c>POST /api/meal-plans/{id}/meals</c> with <c>{ type }</c>: one more meal of that type.</summary>
    [Function("addPlannedMeal")]
    public async Task<HttpResponseData> Add(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "meal-plans/{id:guid}/meals")] HttpRequestData request,
        Guid id, FunctionContext context, CancellationToken cancellationToken)
    {
        var type = Text(await ReadAsync(request, cancellationToken), "type");
        return PlanMealTypes.IsValid(type)
            ? await RespondAsync(request, await plans.AddMealAsync(id, type!, context.GetCurrentUser(), cancellationToken))
            : await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Body must be { \"type\": \"breakfast\" | \"lunch\" | \"dinner\" }." });
    }

    /// <summary><c>DELETE /api/meal-plans/{id}/meals/{mealId}</c>: 200 with the plan and the meal as it was (for Undo).</summary>
    [Function("deletePlannedMeal")]
    public async Task<HttpResponseData> DeleteMeal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "meal-plans/{id:guid}/meals/{mealId}")] HttpRequestData request,
        Guid id, string mealId, CancellationToken cancellationToken) =>
        await RespondAsync(request, await plans.DeleteMealAsync(id, mealId, cancellationToken));

    /// <summary><c>PUT /api/meal-plans/{id}/meals/{mealId}</c> with a meal as <c>DELETE</c> returned it: Undo. Idempotent.</summary>
    [Function("restorePlannedMeal")]
    public async Task<HttpResponseData> Restore(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "meal-plans/{id:guid}/meals/{mealId}")] HttpRequestData request,
        Guid id, string mealId, CancellationToken cancellationToken)
    {
        PlannedMeal? meal;
        try
        {
            meal = MealPlanJson.MealFromNode(JsonNode.Parse(await ReadAsync(request, cancellationToken)));
        }
        catch (JsonException)
        {
            meal = null;
        }

        return meal is null || meal.Id != mealId
            ? await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Body must be the meal, as it was returned." })
            : await RespondAsync(request, await plans.RestoreMealAsync(id, meal, cancellationToken));
    }

    /// <summary><c>PUT /api/meal-plans/{id}/meals/{mealId}/keep</c> with <c>{ keep: true|false }</c>.</summary>
    [Function("keepPlannedMeal")]
    public async Task<HttpResponseData> Keep(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "meal-plans/{id:guid}/meals/{mealId}/keep")] HttpRequestData request,
        Guid id, string mealId, CancellationToken cancellationToken)
    {
        var body = await ReadAsync(request, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("keep", out var keep)
                && keep.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return await RespondAsync(request, await plans.KeepAsync(id, mealId, keep.GetBoolean(), cancellationToken));
            }
        }
        catch (JsonException)
        {
            // falls through to the 400
        }

        return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Body must be { \"keep\": true | false }." });
    }

    // ---- JSON

    internal static (PlanChoices? Choices, string? Error) ParseChoices(byte[] body)
    {
        const string Shape = "Body must be { days, meals, diet, equipment?, preferUseFirst?, comments? }.";
        if (body.Length is 0 || body.Length > MaxBodyBytes)
        {
            return (null, Shape);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("days", out var days) || days.ValueKind != JsonValueKind.Number || !days.TryGetInt32(out var d)
                || !root.TryGetProperty("meals", out var meals) || meals.ValueKind != JsonValueKind.Array)
            {
                return (null, Shape);
            }

            static List<string>? Strings(JsonElement e) =>
                e.ValueKind == JsonValueKind.Array && e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
                    ? [.. e.EnumerateArray().Select(x => x.GetString()!)]
                    : null;
            if (Strings(meals) is not { } mealTypes)
            {
                return (null, Shape);
            }

            IReadOnlyList<string>? equipment = null;
            if (root.TryGetProperty("equipment", out var kit) && kit.ValueKind != JsonValueKind.Null)
            {
                equipment = Strings(kit);
                if (equipment is null)
                {
                    return (null, Shape);
                }
            }

            var diet = root.TryGetProperty("diet", out var dietNode) && dietNode.ValueKind == JsonValueKind.String ? dietNode.GetString()! : "any";
            var preferUseFirst = root.TryGetProperty("preferUseFirst", out var puf) && puf.ValueKind == JsonValueKind.True; // off when missing, as for recipes
            var comments = root.TryGetProperty("comments", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var choices = new PlanChoices(d, mealTypes, diet, equipment, preferUseFirst, comments);
            return MealPlans.Validate(choices) is { } problem ? (null, problem) : (choices, null);
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    private static string? Comments(byte[] body) => Text(body, "comments");

    private static string? Text(byte[] body, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The plan with each meal's facts. The full recipe isn't sent (<c>hasRecipe</c> says if there is one).</summary>
    internal static JsonObject ToJson(MealPlanView view)
    {
        var plan = MealPlanJson.ToNode(view.Plan);
        plan["meals"] = new JsonArray([.. view.Meals.Select(m =>
        {
            var meal = MealPlanJson.ToNode(m.Meal);
            meal.Remove("recipe");
            meal["hasRecipe"] = m.Meal.Recipe is not null;
            meal["usesCount"] = m.UsesCount;
            meal["shares"] = m.Shares is null ? null : JsonSerializer.SerializeToNode(m.Shares, MealPlanJson.Options);
            meal["useFirst"] = JsonSerializer.SerializeToNode(m.UseFirst, MealPlanJson.Options);
            return (JsonNode)meal;
        })]);
        return plan;
    }

    internal static (HttpStatusCode Status, JsonNode Body) ToResponse(PlanOutcome outcome, bool created = false)
    {
        static JsonObject Error(string text) => new() { ["error"] = text };
        return outcome switch
        {
            PlanOutcome.Done d => (created ? HttpStatusCode.Accepted : HttpStatusCode.OK, ToJson(d.View)),
            PlanOutcome.MealDeleted m => (HttpStatusCode.OK, new JsonObject { ["plan"] = ToJson(m.View), ["meal"] = MealPlanJson.ToNode(m.Meal) }),
            PlanOutcome.Invalid i => (HttpStatusCode.BadRequest, Error(i.Error)),
            PlanOutcome.NotFound n => (HttpStatusCode.NotFound, Error(n.Error)),
            PlanOutcome.NotReady n => (HttpStatusCode.Conflict, Error(n.Error)),
            PlanOutcome.Conflict => (HttpStatusCode.Conflict, Error("The plan changed while this was running. Try again.")),
            PlanOutcome.NothingInStock => (HttpStatusCode.UnprocessableEntity, Error("There's nothing in stock to plan from.")),
            PlanOutcome.TooManyRequests => ((HttpStatusCode)429, Error(TooManyMessage)),
            PlanOutcome.Busy => (HttpStatusCode.ServiceUnavailable, Error(MealPlans.BusyText)),
            _ => (HttpStatusCode.ServiceUnavailable, Error(MealPlans.UnavailableText)),
        };
    }

    public const string TooManyMessage = "That's a lot of planning for one hour. Try again a bit later.";

    private static async Task<HttpResponseData> RespondAsync(HttpRequestData request, PlanOutcome outcome, bool created = false)
    {
        var (status, body) = ToResponse(outcome, created);
        var response = await JsonAsync(request, status, body);
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    private static async Task<byte[]> ReadAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData request, HttpStatusCode status, object body)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(body is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(body, MealPlanJson.Options));
        return response;
    }
}

/// <summary>Writes the meals of a new plan (#119). A bad message is logged and dropped; a message delivered twice finds the plan done.</summary>
public sealed partial class MealPlanJobFunction(MealPlans plans, ILogger<MealPlanJobFunction> logger)
{
    [Function("mealPlanJob")]
    public async Task Run(
        [QueueTrigger(StorageQueueMealPlanScheduler.QueueName, Connection = "AzureWebJobsStorage")] string message, CancellationToken cancellationToken)
    {
        Guid? planId;
        try
        {
            planId = StorageQueueMealPlanScheduler.Deserialize(message);
        }
        catch (JsonException)
        {
            planId = null;
        }

        if (planId is { } id)
        {
            await plans.GenerateAsync(id, cancellationToken);
        }
        else
        {
            LogUnreadable();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A meal plan message couldn't be read; dropped.")]
    private partial void LogUnreadable();
}
