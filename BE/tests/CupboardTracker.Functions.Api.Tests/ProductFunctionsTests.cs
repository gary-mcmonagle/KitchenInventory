using System.Net;
using System.Text;
using System.Text.Json;
using CupboardTracker.Application.Products;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Functions.Api.Auth;
using CupboardTracker.Functions.Api.Stock;

namespace CupboardTracker.Functions.Api.Tests;

public sealed class ProductFunctionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid Id = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("CupboardTracker.Functions.Api.Stock.ProductFunctions.Get")]
    [InlineData("CupboardTracker.Functions.Api.Stock.ProductFunctions.Name")]
    public void Both_need_a_signed_in_user(string entryPoint) => Assert.True(FunctionAuthPolicy.RequiresUser(entryPoint));

    [Fact]
    public void Product_json_matches_the_contract()
    {
        var view = new ProductView(Id, "501001234567", ProductStatus.NoMatch, "Unknown item (501001234567)", null, null, 2,
            new DateTimeOffset(2026, 10, 17, 18, 42, 0, TimeSpan.Zero), Role.In);

        Assert.Equal(
            """{"productId":"22222222-2222-2222-2222-222222222222","barcode":"501001234567","status":"noMatch","displayName":"Unknown item (501001234567)","brand":null,"size":null,"inStockCount":2,"latestCheckedInAt":"2026-10-17T18:42:00.000Z","latestDeviceRole":"in"}""",
            JsonSerializer.Serialize(ProductFunctions.ToJson(view), WebJson));
    }

    [Fact]
    public void Parses_the_name_body()
    {
        var (command, error) = ProductFunctions.ParseName(Encoding.UTF8.GetBytes("""{"name":"Smoked paprika","brand":"Schwartz","size":7}"""));

        Assert.Null(error);
        Assert.Equal(new NameProductCommand("Smoked paprika", "Schwartz", null), command);
        Assert.Equal("Body must be a JSON object.", ProductFunctions.ParseName("[]"u8).Error);
        Assert.Equal("Body must be a JSON object.", ProductFunctions.ParseName("nope"u8).Error);
    }

    [Fact]
    public void Name_responses()
    {
        Assert.Equal(HttpStatusCode.OK, ProductFunctions.ToResponse(new NameProductResult.Named(Id, "Smoked paprika")).Status);
        Assert.Equal(HttpStatusCode.NotFound, ProductFunctions.ToResponse(new NameProductResult.NotFound(Id)).Status);
        Assert.Equal(HttpStatusCode.Conflict, ProductFunctions.ToResponse(new NameProductResult.AlreadyNamed(Id, "Paprika")).Status);
        var (status, body) = ProductFunctions.ToResponse(new NameProductResult.Invalid(new Dictionary<string, string> { ["name"] = "Enter a name." }));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("""{"error":"Check the details.","errors":{"name":"Enter a name."}}""", JsonSerializer.Serialize(body, WebJson));
    }
}
