using System.Text;
using CupboardTracker.Application.RunOut;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Stock;

namespace CupboardTracker.Functions.Api.Tests;

// Add to the shopping list when we run out (#89).
public sealed class RunOutFunctionsTests
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("Status")]
    [InlineData("Set")]
    [InlineData("ForBatch")]
    [InlineData("ForProduct")]
    public void All_need_a_signed_in_user(string method) =>
        Assert.True(FunctionAuthPolicy.RequiresUser($"CupboardTracker.Functions.Api.Stock.RunOutFunctions.{method}"));

    [Theory]
    [InlineData("""{"addWhenOut":true}""", true)]
    [InlineData("""{"addWhenOut":false}""", false)]
    [InlineData("""{"addWhenOut":"yes"}""", null)]
    [InlineData("""{}""", null)]
    [InlineData("""[]""", null)]
    [InlineData("""not json""", null)]
    [InlineData("", null)]
    public void The_setting_body_is_a_boolean(string body, bool? expected) =>
        Assert.Equal(expected, RunOutFunctions.Parse(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void The_sheet_body_lists_each_row()
    {
        var id = Guid.NewGuid();
        var json = System.Text.Json.JsonSerializer.Serialize(
            RunOutFunctions.ToBody(new RunOutChoices("Sam", DateTimeOffset.UnixEpoch, [new RunOutChoice(id, "milk", "Milk", 0, false, true, true)])),
            WebJson);

        Assert.Equal(
            $$"""{"checkedOutBy":"Sam","at":"1970-01-01T00:00:00+00:00","items":[{"productId":"{{id}}","genericNameId":"milk","name":"Milk","left":0,"onList":false,"addWhenOut":true,"canSet":true}]}""",
            json);
    }
}
