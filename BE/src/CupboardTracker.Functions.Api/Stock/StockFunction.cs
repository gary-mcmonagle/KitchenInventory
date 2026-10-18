using System.Globalization;
using System.Text.Json;
using CupboardTracker.Application.Stock;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CupboardTracker.Functions.Api.Stock;

/// <summary><c>GET /api/stock</c> (#49): what's in stock, grouped and sorted for the stock list. Needs a signed-in
/// user (the bearer-token middleware answers 401 or 403 before this runs). See docs/api.md.</summary>
public sealed class StockFunction(GetStock getStock)
{
    [Function("stock")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "stock")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var view = await getStock.ExecuteAsync(cancellationToken);
        var response = request.CreateResponse();
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(ToJson(view), WebJson));
        return response;
    }

    internal static object ToJson(StockView view) => new
    {
        generatedAt = Timestamp(view.GeneratedAt),
        totalUnits = view.TotalUnits,
        useFirstCount = view.UseFirstCount,
        products = view.Products.Select(p => new
        {
            productId = p.ProductId,
            barcode = p.Barcode,
            displayName = p.DisplayName,
            brand = p.Brand,
            size = p.Size,
            count = p.Count,
            latestCheckedInAt = Timestamp(p.LatestCheckedInAt),
            status = p.Status switch
            {
                StockProductStatus.Known => "known",
                StockProductStatus.NoMatch => "noMatch",
                _ => "lookupFailed",
            },
            section = p.Section switch
            {
                StockSection.UseFirst => "useFirst",
                StockSection.Later => "later",
                _ => "waitingForName",
            },
            soonestEstimatedExpiryDate = p.SoonestEstimatedExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            daysUntilSoonest = p.DaysUntilSoonest,
            shelfLifeDays = p.ShelfLifeDays,
            openedCount = p.OpenedCount,
            frozenCount = p.FrozenCount,
        }),
    };

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}
