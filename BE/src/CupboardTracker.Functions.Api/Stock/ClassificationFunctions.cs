using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Products;
using CupboardTracker.Functions.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary>A product's aisle (#71) and icon (#72), chosen by hand in Edit details. Both need a signed-in user.</summary>
public sealed class ClassificationFunctions(SetProductAisle setAisle, SetProductIcon setIcon)
{
    /// <summary><c>PUT /api/products/{productId}/aisle</c> with <c>{ "aisle": "&lt;aisle id&gt;" }</c>.</summary>
    [Function("setProductAisle")]
    public async Task<HttpResponseData> Aisle(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "products/{productId:guid}/aisle")] HttpRequestData request,
        Guid productId, FunctionContext context, CancellationToken cancellationToken)
    {
        var (value, error) = await ReadAsync(request, "aisle", allowNull: false, cancellationToken);
        if (error is not null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (status, body) = ToResponse(await setAisle.ExecuteAsync(productId, value, context.GetCurrentUser(), cancellationToken), "aisle");
        return await JsonAsync(request, status, body);
    }

    /// <summary><c>PUT /api/products/{productId}/icon</c> with <c>{ "icon": "&lt;name&gt;" }</c>, or <c>null</c> to let Jev choose.</summary>
    [Function("setProductIcon")]
    public async Task<HttpResponseData> Icon(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "products/{productId:guid}/icon")] HttpRequestData request,
        Guid productId, FunctionContext context, CancellationToken cancellationToken)
    {
        var (value, error) = await ReadAsync(request, "icon", allowNull: true, cancellationToken);
        if (error is not null)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error });
        }

        var (status, body) = ToResponse(await setIcon.ExecuteAsync(productId, value, context.GetCurrentUser(), cancellationToken), "icon");
        return await JsonAsync(request, status, body);
    }

    internal static (string? Value, string? Error) Parse(ReadOnlySpan<byte> body, string field, bool allowNull)
    {
        if (body.Length > ProductFunctions.MaxBodyBytes)
        {
            return (null, "Body is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(field, out var value))
            {
                return (null, $"Body must be {{ \"{field}\": ... }}.");
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => (value.GetString(), null),
                JsonValueKind.Null when allowNull => (null, null),
                _ => (null, $"{field} must be a string{(allowNull ? " or null" : "")}."),
            };
        }
        catch (JsonException)
        {
            return (null, "Body must be a JSON object.");
        }
    }

    internal static (HttpStatusCode Status, object Body) ToResponse(SetClassificationResult result, string field) => result switch
    {
        SetClassificationResult.Saved s => (HttpStatusCode.OK, new Dictionary<string, object?> { ["productId"] = s.ProductId, [field] = s.Value }),
        SetClassificationResult.Invalid i => (HttpStatusCode.BadRequest, new { error = i.Error }),
        SetClassificationResult.NotFound => (HttpStatusCode.NotFound, new { error = "No such product." }),
        SetClassificationResult.Conflict c => (HttpStatusCode.Conflict, new { error = c.Reason }),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static async Task<(string? Value, string? Error)> ReadAsync(
        HttpRequestData request, string field, bool allowNull, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), field, allowNull);
    }

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData request, HttpStatusCode status, object body)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, WebJson));
        return response;
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}
