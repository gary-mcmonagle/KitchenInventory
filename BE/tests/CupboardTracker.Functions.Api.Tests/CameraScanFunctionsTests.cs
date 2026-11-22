using System.Net;
using System.Text;
using System.Text.Json;
using CupboardTracker.Application.Stock;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Stock;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class CameraScanFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid Unit = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Product = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("CupboardTracker.Functions.Api.Stock.CameraScanFunctions.Scan")]
    [InlineData("CupboardTracker.Functions.Api.Stock.CameraScanFunctions.Undo")]
    public void Both_need_a_signed_in_user(string entryPoint) => Assert.True(FunctionAuthPolicy.RequiresUser(entryPoint));

    [Fact]
    public void The_body_is_a_request_id_a_barcode_and_a_role()
    {
        static ((Guid, string?, string?) Value, string? Error) Parse(string json) =>
            CameraScanFunctions.Parse(Encoding.UTF8.GetBytes(json));

        var ok = Parse($$"""{"requestId":"{{Unit}}","barcode":"5000354922831","role":"in"}""");
        Assert.Null(ok.Error);
        Assert.Equal((Unit, "5000354922831", "in"), ok.Value);

        Assert.NotNull(Parse("""{"barcode":"5000354922831","role":"in"}""").Error);
        Assert.NotNull(Parse("""{"requestId":"nope","barcode":"5000354922831","role":"in"}""").Error);
        Assert.NotNull(Parse($$"""{"requestId":"{{Unit}}","barcode":5000354922831,"role":"in"}""").Error);
        Assert.NotNull(Parse($$"""{"requestId":"{{Unit}}","barcode":"5000354922831"}""").Error);
        Assert.NotNull(Parse("[]").Error);
        Assert.NotNull(Parse("not json").Error);
        Assert.Equal("Body is too large.", CameraScanFunctions.Parse(new byte[CameraScanFunctions.MaxBodyBytes + 1]).Error);
    }

    [Fact]
    public void Scan_responses()
    {
        string Json(CameraScanResult r) => JsonSerializer.Serialize(CameraScanFunctions.ToResponse(r).Body, WebJson);

        Assert.Equal(
            """{"outcome":"checkedIn","unitId":"11111111-1111-1111-1111-111111111111","productId":"22222222-2222-2222-2222-222222222222","displayName":"Beef Gravy","count":2}""",
            Json(new CameraScanResult.CheckedIn(Unit, Product, "Beef Gravy", 2)));
        Assert.Equal(
            """{"outcome":"needsName","unitId":"11111111-1111-1111-1111-111111111111","productId":"22222222-2222-2222-2222-222222222222","barcode":"5000354922831"}""",
            Json(new CameraScanResult.NeedsName(Unit, Product, "5000354922831")));
        Assert.Equal(
            """{"outcome":"checkedOut","unitId":"11111111-1111-1111-1111-111111111111","productId":"22222222-2222-2222-2222-222222222222","displayName":"Beef Gravy","remainingCount":1}""",
            Json(new CameraScanResult.CheckedOut(Unit, Product, "Beef Gravy", 1)));
        Assert.Equal("""{"outcome":"notInStock","barcode":"5000354922831"}""", Json(new CameraScanResult.NotInStock("5000354922831")));
        Assert.Equal(HttpStatusCode.OK, CameraScanFunctions.ToResponse(new CameraScanResult.NotInStock("x")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, CameraScanFunctions.ToResponse(new CameraScanResult.Invalid("x")).Status);
    }

    [Fact]
    public void Undo_responses()
    {
        var (status, body) = CameraScanFunctions.ToResponse(new UndoCheckInResult.Undone(Unit, Product, true));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """{"unitId":"11111111-1111-1111-1111-111111111111","productId":"22222222-2222-2222-2222-222222222222","undone":true}""",
            JsonSerializer.Serialize(body, WebJson));
        Assert.Equal(HttpStatusCode.NotFound, CameraScanFunctions.ToResponse(new UndoCheckInResult.NotFound(Unit)).Status);
        Assert.Equal(HttpStatusCode.Conflict, CameraScanFunctions.ToResponse(new UndoCheckInResult.NotUndoable("x")).Status);
    }
}
