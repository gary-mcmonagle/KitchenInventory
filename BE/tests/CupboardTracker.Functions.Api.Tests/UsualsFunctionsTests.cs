using System.Net;
using System.Text.Json;
using CupboardTracker.Application.Usuals;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.ShoppingList;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class UsualsFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid Job = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Product = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("CupboardTracker.Functions.Api.ShoppingList.UsualsFunctions.Start")]
    [InlineData("CupboardTracker.Functions.Api.ShoppingList.UsualsFunctions.Get")]
    public void Both_need_a_signed_in_user(string entryPoint) => Assert.True(FunctionAuthPolicy.RequiresUser(entryPoint));

    [Fact]
    public void A_started_job_is_202_and_working_a_rate_limited_one_is_429()
    {
        var job = new UsualsJob(Job, Guid.NewGuid(), UsualsStatus.Working, UsualsSteps.History, null, [], DateTimeOffset.UnixEpoch);

        var (status, body) = UsualsFunctions.ToResponse(new UsualsStartResult.Started(job));

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("""{"jobId":"11111111-1111-1111-1111-111111111111","status":"working","step":"history","method":null,"items":[]}""",
            JsonSerializer.Serialize(body, WebJson));
        Assert.Equal(HttpStatusCode.TooManyRequests, UsualsFunctions.ToResponse(new UsualsStartResult.RateLimited()).Status);
    }

    [Fact]
    public void A_ready_job_lists_every_item_with_its_band_and_why_line()
    {
        var job = new UsualsJob(Job, Guid.NewGuid(), UsualsStatus.Ready, UsualsSteps.List, UsualsMethods.Pattern,
            [
                new UsualsItem("Medium eggs", "g1", "dairyEggs", UsualsBand.Due, "Bought in 4 of the last 4 weeks", Product),
                new UsualsItem("Squash", null, "fruitVeg", UsualsBand.Maybe, "Bought in 2 of the last 4 weeks", Product),
            ], DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(UsualsFunctions.Wire(job), WebJson);

        Assert.Equal(
            """{"jobId":"11111111-1111-1111-1111-111111111111","status":"ready","step":"list","method":"pattern","items":["""
            + """{"name":"Medium eggs","genericNameId":"g1","aisle":"dairyEggs","band":"due","why":"Bought in 4 of the last 4 weeks","productId":"22222222-2222-2222-2222-222222222222"},"""
            + """{"name":"Squash","genericNameId":null,"aisle":"fruitVeg","band":"maybe","why":"Bought in 2 of the last 4 weeks","productId":"22222222-2222-2222-2222-222222222222"}]}""",
            json);
    }
}
