using System.Net;
using CupboardTracker.Application.Products;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Stock;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class ClassificationFunctionsTests
{
    [Theory]
    [InlineData("Aisle")]
    [InlineData("Icon")]
    public void Both_need_a_signed_in_user(string method) =>
        Assert.True(FunctionAuthPolicy.RequiresUser($"CupboardTracker.Functions.Api.Stock.ClassificationFunctions.{method}"));

    [Fact]
    public void Bodies_and_responses()
    {
        Assert.Equal(("bakery", null), ClassificationFunctions.Parse("""{"aisle":"bakery"}"""u8, "aisle", allowNull: false));
        Assert.NotNull(ClassificationFunctions.Parse("""{"aisle":null}"""u8, "aisle", allowNull: false).Error);
        Assert.Equal((null, null), ClassificationFunctions.Parse("""{"icon":null}"""u8, "icon", allowNull: true));
        Assert.NotNull(ClassificationFunctions.Parse("""{}"""u8, "icon", allowNull: true).Error);
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, ClassificationFunctions.ToResponse(new SetClassificationResult.Saved(id, "bread"), "icon").Status);
        Assert.Equal(HttpStatusCode.BadRequest, ClassificationFunctions.ToResponse(new SetClassificationResult.Invalid("x"), "icon").Status);
        Assert.Equal(HttpStatusCode.NotFound, ClassificationFunctions.ToResponse(new SetClassificationResult.NotFound(id), "icon").Status);
        Assert.Equal(HttpStatusCode.Conflict, ClassificationFunctions.ToResponse(new SetClassificationResult.Conflict(id, "x"), "aisle").Status);
    }
}
