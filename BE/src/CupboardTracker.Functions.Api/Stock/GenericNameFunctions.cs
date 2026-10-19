using System.Net;
using System.Text.Json;
using CupboardTracker.Application.GenericNaming;
using CupboardTracker.Domain.Products;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>Generic names (#90): the list for Edit details' picker, and choosing one by hand. Both need a signed-in user.</summary>
public sealed class GenericNameFunctions(ListGenericNames list, SetProductGenericName set)
{
    /// <summary><c>GET /api/generic-names</c> → <c>{ genericNames: [{ id, name, aisleId, aisleName, productCount }] }</c>, A–Z.</summary>
    [Function("genericNames")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "generic-names")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var names = await list.ExecuteAsync(cancellationToken);
        return await JsonAsync(request, HttpStatusCode.OK, new
        {
            genericNames = names.Select(n => new { id = n.Id, name = n.Name, aisleId = n.AisleId, aisleName = n.AisleName, productCount = n.ProductCount }),
        });
    }

    /// <summary><c>PUT /api/products/{productId}/generic-name</c> with <c>{ "genericNameId": "…" }</c> (an existing
    /// name) or <c>{ "name": "…" }</c> (a new one). Saved as manual.</summary>
    [Function("setProductGenericName")]
    public async Task<HttpResponseData> Set(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "products/{productId:guid}/generic-name")] HttpRequestData request,
        Guid productId, FunctionContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var (id, name, error) = Parse(buffer.ToArray());
        if (error is not null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (status, body) = ToResponse(await set.ExecuteAsync(productId, id, name, context.GetCurrentUser(), cancellationToken));
        return await JsonAsync(request, status, body);
    }

    internal static (string? Id, string? Name, string? Error) Parse(byte[] body)
    {
        if (body.Length is 0 or > 4096)
        {
            return (null, null, "Body must be { genericNameId } or { name }.");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, "Body must be a JSON object.");
            }

            static string? Text(JsonElement root, string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            return (Text(root, "genericNameId"), Text(root, "name"), null);
        }
        catch (JsonException)
        {
            return (null, null, "Body must be a JSON object.");
        }
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(SetGenericNameResult result) => result switch
    {
        SetGenericNameResult.Saved s => (HttpStatusCode.OK, new
        {
            productId = s.ProductId,
            genericName = new { id = s.Generic.Id, name = s.Generic.Name, source = GenericNames.SourceName(s.Generic.Source) },
            created = s.Created,
        }),
        SetGenericNameResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        SetGenericNameResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such known product." }),
        SetGenericNameResult.Conflict c => (HttpStatusCode.Conflict, new { error = c.Reason }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
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
