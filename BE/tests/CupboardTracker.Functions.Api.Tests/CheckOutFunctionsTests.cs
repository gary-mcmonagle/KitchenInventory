using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Stock;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Stock;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class CheckOutFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid Unit = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Product = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("CupboardTracker.Functions.Api.Stock.CheckOutFunctions.CheckOut")]
    [InlineData("CupboardTracker.Functions.Api.Stock.CheckOutFunctions.Undo")]
    public void Both_need_a_signed_in_user(string entryPoint) => Assert.True(FunctionAuthPolicy.RequiresUser(entryPoint));

    [Fact]
    public void Check_out_responses()
    {
        var (status, body) = CheckOutFunctions.ToResponse(new CheckOutFromAppResult.CheckedOut(Unit, Product, "Beef Gravy", 1));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """{"unitId":"11111111-1111-1111-1111-111111111111","productId":"22222222-2222-2222-2222-222222222222","displayName":"Beef Gravy","remainingCount":1}""",
            JsonSerializer.Serialize(body, WebJson));

        Assert.Equal(HttpStatusCode.Conflict, CheckOutFunctions.ToResponse(new CheckOutFromAppResult.NothingInStock(Product)).Status);
    }

    [Fact]
    public void Undo_responses()
    {
        var (status, body) = CheckOutFunctions.ToResponse(new UndoCheckOutResult.InStock(Unit, Product, 2, true));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """{"unitId":"11111111-1111-1111-1111-111111111111","productId":"22222222-2222-2222-2222-222222222222","count":2,"restored":true}""",
            JsonSerializer.Serialize(body, WebJson));

        Assert.Equal(HttpStatusCode.NotFound, CheckOutFunctions.ToResponse(new UndoCheckOutResult.NotFound(Unit)).Status);
    }
}
