using System.Net;
using System.Text;
using System.Text.Json;
using CupboardTracker.Application.Adding;
using CupboardTracker.Functions.Api.Adding;
using CupboardTracker.Functions.Api.Auth;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class AddingFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static string Serialize(object body) => JsonSerializer.Serialize(body, WebJson);

    [Theory]
    [InlineData("Recognise")]
    [InlineData("Loose")]
    [InlineData("Add")]
    [InlineData("Assign")]
    [InlineData("DismissStoreLabel")]
    public void All_need_a_signed_in_user(string method) =>
        Assert.True(FunctionAuthPolicy.RequiresUser($"CupboardTracker.Functions.Api.Adding.AddingFunctions.{method}"));

    [Fact]
    public void An_add_request_parses_each_kind_of_row()
    {
        var body = """
            {"requestId":"11111111-1111-1111-1111-111111111111","items":[
              {"productId":"22222222-2222-2222-2222-222222222222","quantity":2,"source":"pick"},
              {"name":"Leeks","quantity":3,"source":"photo"},
              {"name":"Baguette","brand":"Gail's","size":null}
            ]}
            """;

        var (requestId, items, error) = AddingFunctions.ParseAdd(Encoding.UTF8.GetBytes(body));

        Assert.Null(error);
        Assert.Equal(Id, requestId);
        Assert.Equal(
            [
                new AddItem(Guid.Parse("22222222-2222-2222-2222-222222222222"), null, null, null, 2, AddSource.Pick),
                new AddItem(null, "Leeks", null, null, 3, AddSource.Photo),
                new AddItem(null, "Baguette", "Gail's", null, 1, AddSource.Typed),
            ],
            items!);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{"requestId":"not-a-uuid","items":[]}""")]
    [InlineData("""{"requestId":"00000000-0000-0000-0000-000000000000","items":[]}""")]
    [InlineData("""{"requestId":"11111111-1111-1111-1111-111111111111","items":[{"productId":"x"}]}""")]
    [InlineData("""{"requestId":"11111111-1111-1111-1111-111111111111","items":[{"name":"x","quantity":"2"}]}""")]
    [InlineData("""{"requestId":"11111111-1111-1111-1111-111111111111","items":[{"name":"x","source":"scanner"}]}""")]
    [InlineData("""not json""")]
    public void Bad_add_requests_are_rejected_with_a_message(string body)
    {
        var (_, items, error) = AddingFunctions.ParseAdd(Encoding.UTF8.GetBytes(body));
        Assert.Null(items);
        Assert.NotNull(error);
    }

    [Fact]
    public void An_assign_body_defaults_to_a_pick()
    {
        Assert.Equal(new AddItem(Id, null, null, null, 1, AddSource.Pick),
            AddingFunctions.ParseItem(Encoding.UTF8.GetBytes("""{"productId":"11111111-1111-1111-1111-111111111111"}""")).Item);
        Assert.Equal(new AddItem(null, "Ham hock terrine", null, null, 1, AddSource.Typed),
            AddingFunctions.ParseItem(Encoding.UTF8.GetBytes("""{"name":"Ham hock terrine","source":"typed"}""")).Item);
    }

    [Fact]
    public void Recognition_json_matches_the_contract()
    {
        var (status, body) = AddingFunctions.ToResponse(new RecogniseProductPhotoResult.Recognised(true,
        [
            new RecognisedItemView(new RecognisedItem("Leeks", null, null, ItemKind.Loose, 2), Id, "Leeks"),
            new RecognisedItemView(new RecognisedItem("Pitted green olives", "Crespo", "340 g", ItemKind.Packaged, 1), null, null),
        ]));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""
            {"available":true,"readable":true,"items":[
            {"name":"Leeks","brand":null,"size":null,"kind":"loose","quantity":2,"match":{"productId":"11111111-1111-1111-1111-111111111111","displayName":"Leeks"}},
            {"name":"Pitted green olives","brand":"Crespo","size":"340 g","kind":"packaged","quantity":1,"match":null}]}
            """.ReplaceLineEndings(""), Serialize(body));
        var (unavailableStatus, unavailable) = AddingFunctions.ToResponse(new RecogniseProductPhotoResult.Unavailable());
        Assert.Equal(HttpStatusCode.OK, unavailableStatus); // not a 5xx: that would fire the error alert
        Assert.StartsWith("""{"available":false,"readable":false,"items":[],"error":""", Serialize(unavailable), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, AddingFunctions.ToResponse(new RecogniseProductPhotoResult.Invalid("x")).Status);
    }

    [Fact]
    public void Add_and_assign_responses()
    {
        var (status, body) = AddingFunctions.ToResponse(new AddToStockResult.Added([new AddedItem(Id, "Leeks", 3, true)]));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"units":3,"items":[{"productId":"11111111-1111-1111-1111-111111111111","displayName":"Leeks","quantity":3,"createdProduct":true}]}""",
            Serialize(body));

        Assert.Equal(HttpStatusCode.OK, AddingFunctions.ToResponse(new AssignStoreLabelResult.Assigned(Id, Id, Id, "Ham")).Status);
        Assert.Equal(HttpStatusCode.NotFound, AddingFunctions.ToResponse(new AssignStoreLabelResult.NotFound(Id)).Status);
        Assert.Equal(HttpStatusCode.Conflict, AddingFunctions.ToResponse(new AssignStoreLabelResult.AlreadyAssigned(Id, Id)).Status);
        Assert.Equal(HttpStatusCode.BadRequest,
            AddingFunctions.ToResponse(new AssignStoreLabelResult.Invalid(new Dictionary<string, string> { ["name"] = "Enter a name." })).Status);
    }
}
