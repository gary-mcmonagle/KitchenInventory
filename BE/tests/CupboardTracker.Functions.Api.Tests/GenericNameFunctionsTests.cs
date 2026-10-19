using System.Net;
using System.Text;
using CupboardTracker.Application.GenericNaming;
using CupboardTracker.Domain.Products;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Stock;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class GenericNameFunctionsTests
{
    [Theory]
    [InlineData("List")]
    [InlineData("Set")]
    public void Both_need_a_signed_in_user(string method) =>
        Assert.True(FunctionAuthPolicy.RequiresUser($"CupboardTracker.Functions.Api.Stock.GenericNameFunctions.{method}"));

    [Fact]
    public void Bodies_and_responses()
    {
        Assert.Equal(("milk", null, null), GenericNameFunctions.Parse(Encoding.UTF8.GetBytes("""{"genericNameId":"milk"}""")));
        Assert.Equal((null, "Hot honey", null), GenericNameFunctions.Parse(Encoding.UTF8.GetBytes("""{"name":"Hot honey"}""")));
        Assert.NotNull(GenericNameFunctions.Parse(Encoding.UTF8.GetBytes("[]")).Error);
        Assert.NotNull(GenericNameFunctions.Parse([]).Error);

        var generic = new ProductGenericName("milk", "Milk", GenericNameSource.Manual, null, DateTimeOffset.UnixEpoch);
        Assert.Equal(HttpStatusCode.OK, GenericNameFunctions.ToResponse(new SetGenericNameResult.Saved(Guid.NewGuid(), generic, false)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, GenericNameFunctions.ToResponse(new SetGenericNameResult.Invalid("x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, GenericNameFunctions.ToResponse(new SetGenericNameResult.NotFound()).Status);
        Assert.Equal(HttpStatusCode.Conflict, GenericNameFunctions.ToResponse(new SetGenericNameResult.Conflict("x")).Status);
    }
}
