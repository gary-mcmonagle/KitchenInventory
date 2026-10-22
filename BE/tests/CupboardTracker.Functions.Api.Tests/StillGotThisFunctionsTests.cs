using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Products;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Stock;

namespace CupboardTracker.Functions.Api.Tests;

// "Still got this?" (#94): the endpoints' auth and JSON.
public sealed class StillGotThisFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid Unit = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Product = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("CupboardTracker.Functions.Api.Stock.StillGotThisFunctions.StillHere")]
    [InlineData("CupboardTracker.Functions.Api.Stock.StillGotThisFunctions.UndoStillHere")]
    [InlineData("CupboardTracker.Functions.Api.Stock.StillGotThisFunctions.Gone")]
    [InlineData("CupboardTracker.Functions.Api.Stock.StillGotThisFunctions.Expired")]
    public void All_need_a_signed_in_user(string entryPoint) => Assert.True(FunctionAuthPolicy.RequiresUser(entryPoint));

    [Fact]
    public void Still_here_responses()
    {
        var (status, body) = StillGotThisFunctions.ToResponse(
            new StillHereResult.Saved(Unit, Product, new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero)));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """{"unitId":"11111111-1111-1111-1111-111111111111","productId":"22222222-2222-2222-2222-222222222222","lastConfirmedAt":"2026-10-20T17:00:00.000Z"}""",
            JsonSerializer.Serialize(body, WebJson));

        Assert.Equal(HttpStatusCode.NotFound, StillGotThisFunctions.ToResponse(new StillHereResult.NotFound(Unit)).Status);
        Assert.Equal(HttpStatusCode.Conflict, StillGotThisFunctions.ToResponse(new StillHereResult.NotInStock(Unit)).Status);
    }

    [Fact]
    public void Expired_units_json()
    {
        var json = JsonSerializer.Serialize(StillGotThisFunctions.ToJson(
        [
            new ExpiredUnit(Unit, Product, "Hummus", Storage.Fridge, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 21), 4),
        ]), WebJson);

        Assert.Equal(
            """{"units":[{"unitId":"11111111-1111-1111-1111-111111111111","productId":"22222222-2222-2222-2222-222222222222","name":"Hummus","storage":"fridge","boughtOn":"2026-10-10","openedOn":"2026-10-18","estimatedExpiryDate":"2026-10-21","daysOver":4}]}""",
            json);
    }
}
