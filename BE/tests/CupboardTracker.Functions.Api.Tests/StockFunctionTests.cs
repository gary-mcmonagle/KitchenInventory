using System.Text.Json;
using CupboardTracker.Application.Stock;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Stock;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class StockFunctionTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void The_stock_endpoint_needs_a_signed_in_user() =>
        Assert.True(FunctionAuthPolicy.RequiresUser("CupboardTracker.Functions.Api.Stock.StockFunction.Run"));

    [Fact]
    public void Json_matches_the_contract()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var view = new StockView(
            new DateTimeOffset(2026, 10, 17, 22, 0, 0, TimeSpan.Zero),
            3,
            [
                new StockProduct(id, "5000354922831", "Beef Gravy", "Bisto", null, 2,
                    new DateTimeOffset(2026, 10, 17, 18, 16, 15, 743, TimeSpan.Zero), StockProductStatus.Known,
                    StockSection.UseFirst, new DateOnly(2026, 10, 19), 2, 365, OpenedCount: 1, FrozenCount: 1, SoonestState: "opened", SoonestStateOn: new DateOnly(2026, 10, 15)),
                new StockProduct(id, "20055363", "Unknown item (20055363)", null, null, 1,
                    new DateTimeOffset(2026, 10, 17, 18, 0, 0, TimeSpan.Zero), StockProductStatus.NoMatch,
                    StockSection.WaitingForName),
            ],
            1);

        var json = JsonSerializer.Serialize(StockFunction.ToJson(view), WebJson);

        Assert.Equal(
            """{"generatedAt":"2026-10-17T22:00:00.000Z","totalUnits":3,"useFirstCount":1,"products":[""" +
            """{"productId":"11111111-1111-1111-1111-111111111111","barcode":"5000354922831","displayName":"Beef Gravy","brand":"Bisto","size":null,"count":2,"latestCheckedInAt":"2026-10-17T18:16:15.743Z","status":"known","section":"useFirst","soonestEstimatedExpiryDate":"2026-10-19","daysUntilSoonest":2,"shelfLifeDays":365,"openedCount":1,"frozenCount":1,"soonestState":"opened","soonestStateOn":"2026-10-15","aisleId":"other","genericName":null,"icon":null,"useFirst":true},""" +
            """{"productId":"11111111-1111-1111-1111-111111111111","barcode":"20055363","displayName":"Unknown item (20055363)","brand":null,"size":null,"count":1,"latestCheckedInAt":"2026-10-17T18:00:00.000Z","status":"noMatch","section":"waitingForName","soonestEstimatedExpiryDate":null,"daysUntilSoonest":null,"shelfLifeDays":null,"openedCount":0,"frozenCount":0,"soonestState":null,"soonestStateOn":null,"aisleId":"other","genericName":null,"icon":null,"useFirst":false}],"aisles":[],"waitingForName":[""" +
            """{"productId":"11111111-1111-1111-1111-111111111111","barcode":"20055363","displayName":"Unknown item (20055363)","brand":null,"size":null,"count":1,"latestCheckedInAt":"2026-10-17T18:00:00.000Z","status":"noMatch","section":"waitingForName","soonestEstimatedExpiryDate":null,"daysUntilSoonest":null,"shelfLifeDays":null,"openedCount":0,"frozenCount":0,"soonestState":null,"soonestStateOn":null,"aisleId":"other","genericName":null,"icon":null,"useFirst":false}]}""",
            json);
    }
}
