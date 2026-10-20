using System.Net;
using System.Text.Json;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Functions.Api.ShoppingList;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class ShoppingListFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Bodies_must_be_json_objects(string body) =>
        Assert.NotNull(ShoppingListFunctions.Parse(System.Text.Encoding.UTF8.GetBytes(body)).Error);

    [Fact]
    public void Results_map_to_statuses_and_the_item_shape()
    {
        var item = new ShoppingListItem(Guid.NewGuid(), "Bread", "bread", null, null, null, null, "typed", ListItemStatus.Open,
            Guid.NewGuid(), "Gary", DateTimeOffset.UnixEpoch, null, null, null, DateTimeOffset.UnixEpoch, 1, "c");

        var (status, body) = ShoppingListFunctions.ToResponse(new ListChangeResult.Changed(item, Merged: true));

        Assert.Equal(HttpStatusCode.OK, status);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(body, WebJson)).RootElement;
        Assert.True(json.GetProperty("merged").GetBoolean());
        Assert.Equal(("Bread", true, "open"), (json.GetProperty("item").GetProperty("name").GetString(),
            json.GetProperty("item").GetProperty("findingAisle").GetBoolean(), json.GetProperty("item").GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, ShoppingListFunctions.ToResponse(new ListChangeResult.Invalid("x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, ShoppingListFunctions.ToResponse(new ListChangeResult.NotFound()).Status);
    }
}
