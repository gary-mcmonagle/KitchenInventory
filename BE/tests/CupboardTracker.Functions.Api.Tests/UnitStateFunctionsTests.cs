using System.Net;
using CupboardTracker.Application.Products;
using CupboardTracker.Application.Stock;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Stock;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class UnitStateFunctionsTests
{
    [Theory]
    [InlineData("Opened")]
    [InlineData("Frozen")]
    [InlineData("Defrosted")]
    [InlineData("Undo")]
    [InlineData("OpenedShelfLife")]
    [InlineData("OpenNext")]
    public void All_need_a_signed_in_user(string method) =>
        Assert.True(FunctionAuthPolicy.RequiresUser($"CupboardTracker.Functions.Api.Stock.UnitStateFunctions.{method}"));

    [Fact]
    public void The_date_is_optional()
    {
        Assert.Equal((null, null), UnitStateFunctions.ParseOptionalDate(""u8));
        Assert.Equal((null, null), UnitStateFunctions.ParseOptionalDate("{}"u8));
        Assert.Equal((null, null), UnitStateFunctions.ParseOptionalDate("""{"date":null}"""u8));
        Assert.Equal(("2026-10-12", null), UnitStateFunctions.ParseOptionalDate("""{"date":"2026-10-12"}"""u8));
        Assert.NotNull(UnitStateFunctions.ParseOptionalDate("""{"date":12}"""u8).Error);
        Assert.NotNull(UnitStateFunctions.ParseOptionalDate("[]"u8).Error);
        Assert.NotNull(UnitStateFunctions.ParseOptionalDate("nope"u8).Error);
    }

    [Fact]
    public void Responses()
    {
        var id = Guid.NewGuid();
        var unit = new ProductUnitView(id, DateTimeOffset.UnixEpoch, null, null, null);
        Assert.Equal(HttpStatusCode.OK, UnitStateFunctions.ToResponse(new ChangeUnitStateResult.Changed(id, unit)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, UnitStateFunctions.ToResponse(new ChangeUnitStateResult.Invalid("x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, UnitStateFunctions.ToResponse(new ChangeUnitStateResult.NotFound(id)).Status);
        Assert.Equal(HttpStatusCode.Conflict, UnitStateFunctions.ToResponse(new ChangeUnitStateResult.Conflict(id, "That unit isn't frozen.")).Status);
    }
}
