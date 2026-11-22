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
    [InlineData("CupboardTracker.Functions.Api.Stock.CheckOutFunctions.CheckOutOne")]
    [InlineData("CupboardTracker.Functions.Api.Stock.CheckOutFunctions.SetExpiry")]
    [InlineData("CupboardTracker.Functions.Api.Stock.CheckOutFunctions.CheckOutMany")]
    public void All_need_a_signed_in_user(string entryPoint) => Assert.True(FunctionAuthPolicy.RequiresUser(entryPoint));

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
    public void Unit_check_out_and_expiry_responses()
    {
        var (status, body) = CheckOutFunctions.ToResponse(new CheckOutUnitResult.CheckedOut(Unit, Product, "Beef Gravy", 0));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """{"unitId":"11111111-1111-1111-1111-111111111111","productId":"22222222-2222-2222-2222-222222222222","displayName":"Beef Gravy","remainingCount":0}""",
            JsonSerializer.Serialize(body, WebJson));
        Assert.Equal(HttpStatusCode.Conflict, CheckOutFunctions.ToResponse(new CheckOutUnitResult.NotInStock(Unit)).Status);
        Assert.Equal(HttpStatusCode.NotFound, CheckOutFunctions.ToResponse(new CheckOutUnitResult.NotFound(Unit)).Status);

        var (dateStatus, dateBody) = CheckOutFunctions.ToResponse(new SetUnitExpiryResult.Saved(Unit, Product, new DateOnly(2026, 10, 20)));
        Assert.Equal(HttpStatusCode.OK, dateStatus);
        Assert.Equal(
            """{"unitId":"11111111-1111-1111-1111-111111111111","productId":"22222222-2222-2222-2222-222222222222","estimatedExpiryDate":"2026-10-20","expirySource":"manual"}""",
            JsonSerializer.Serialize(dateBody, WebJson));
        Assert.Equal(HttpStatusCode.BadRequest, CheckOutFunctions.ToResponse(new SetUnitExpiryResult.Invalid("x")).Status);
        Assert.Equal(HttpStatusCode.Conflict, CheckOutFunctions.ToResponse(new SetUnitExpiryResult.Conflict(Unit, "x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, CheckOutFunctions.ToResponse(new SetUnitExpiryResult.NotFound(Unit)).Status);
    }

    [Fact]
    public void Parses_the_date_body()
    {
        Assert.Equal(("2026-10-20", null), CheckOutFunctions.ParseDate("""{"date":"2026-10-20"}"""u8));
        Assert.NotNull(CheckOutFunctions.ParseDate("""{"date":20261020}"""u8).Error);
        Assert.NotNull(CheckOutFunctions.ParseDate("[]"u8).Error);
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

    [Fact]
    public void Clear_out_body_is_a_list_of_unit_ids()
    {
        static (IReadOnlyList<Guid>? Ids, string? Error) Parse(string json) =>
            CheckOutFunctions.ParseUnitIds(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Equal([Unit, Product], Parse($$"""{"unitIds":["{{Unit}}","{{Product}}"]}""").Ids);
        Assert.Empty(Parse("""{"unitIds":[]}""").Ids!); // the use case refuses an empty list
        Assert.NotNull(Parse("""{"unitIds":["nope"]}""").Error);
        Assert.NotNull(Parse("""{"unitIds":[1]}""").Error);
        Assert.NotNull(Parse("""{"unitIds":"x"}""").Error);
        Assert.NotNull(Parse("""{}""").Error);
        Assert.NotNull(Parse("""[]""").Error);
        Assert.NotNull(Parse("not json").Error);
        Assert.Equal("Body is too large.", CheckOutFunctions.ParseUnitIds(new byte[CheckOutFunctions.MaxClearOutBodyBytes + 1]).Error);
    }

    [Fact]
    public void Clear_out_responses()
    {
        var (status, body) = CheckOutFunctions.ToResponse(new CheckOutManyResult.Done(
        [
            new ClearOutUnitResult(Unit, ClearOutOutcome.CheckedOut, Product),
            new ClearOutUnitResult(Product, ClearOutOutcome.Unchanged),
            new ClearOutUnitResult(Guid.Empty, ClearOutOutcome.Failed),
        ]));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """{"checkedOut":1,"unchanged":1,"failed":1,"units":[{"unitId":"11111111-1111-1111-1111-111111111111","outcome":"checkedOut","productId":"22222222-2222-2222-2222-222222222222"},{"unitId":"22222222-2222-2222-2222-222222222222","outcome":"unchanged","productId":null},{"unitId":"00000000-0000-0000-0000-000000000000","outcome":"failed","productId":null}]}""",
            JsonSerializer.Serialize(body, WebJson));

        Assert.Equal(HttpStatusCode.BadRequest, CheckOutFunctions.ToResponse(new CheckOutManyResult.Invalid("x")).Status);
    }
}
