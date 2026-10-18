using System.Net;
using System.Text;
using System.Text.Json;
using CupboardTracker.Application.Products;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
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
    [InlineData("CupboardTracker.Functions.Api.Stock.ProductFunctions.EditDetails")]
    [InlineData("CupboardTracker.Functions.Api.Stock.ProductFunctions.ShelfLife")]
    public void All_need_a_signed_in_user(string entryPoint) => Assert.True(FunctionAuthPolicy.RequiresUser(entryPoint));

    [Fact]
    public void Product_json_matches_the_contract()
    {
        var view = new ProductView(Id, "501001234567", ProductStatus.NoMatch, "Unknown item (501001234567)", null, null, 2,
            new DateTimeOffset(2026, 10, 17, 18, 42, 0, TimeSpan.Zero), Role.In,
            [
                new ProductUnitView(Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero), new DateOnly(2026, 10, 18), ExpirySource.Opened, 1,
                    OpenedOn: new DateOnly(2026, 10, 17), CurrentStorage: Storage.Fridge, CanUndoStateChange: true),
                new ProductUnitView(Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    new DateTimeOffset(2026, 10, 17, 18, 42, 0, TimeSpan.Zero), null, null, null),
            ],
            7, "jev",
            StateShelfLife.Manual(5, Storage.Fridge), new StateShelfLife(90, Storage.Freezer, 0.7, false, "jev"), null,
            Storage.Cupboard);

        Assert.Equal(
            """{"productId":"22222222-2222-2222-2222-222222222222","barcode":"501001234567","status":"noMatch","displayName":"Unknown item (501001234567)","brand":null,"size":null,"inStockCount":2,"latestCheckedInAt":"2026-10-17T18:42:00.000Z","latestDeviceRole":"in",""" +
            "\"shelfLifeDays\":7,\"shelfLifeSource\":\"jev\",\"usualStorage\":\"cupboard\",\"opened\":" +
            """{"days":5,"storage":"fridge","source":"manual"},"frozen":{"days":90,"storage":"freezer","source":"jev"},"defrosted":null,"openedCount":1,"frozenCount":0,"aisleId":null,"aisleSource":null,"icon":null,"iconSource":null,"units":[""" +
            """{"unitId":"33333333-3333-3333-3333-333333333333","checkedInAt":"2026-10-13T09:00:00.000Z","estimatedExpiryDate":"2026-10-18","expirySource":"opened","daysUntilExpiry":1,"openedOn":"2026-10-17","frozenOn":null,"defrostedOn":null,"currentStorage":"fridge","estimatePending":false,"canUndoStateChange":true},""" +
            """{"unitId":"44444444-4444-4444-4444-444444444444","checkedInAt":"2026-10-17T18:42:00.000Z","estimatedExpiryDate":null,"expirySource":null,"daysUntilExpiry":null,"openedOn":null,"frozenOn":null,"defrostedOn":null,"currentStorage":null,"estimatePending":false,"canUndoStateChange":false}]}""",
            JsonSerializer.Serialize(ProductFunctions.ToJson(view), WebJson));
    }

    [Fact]
    public void Parses_the_shelf_life_body()
    {
        Assert.Equal((14, null), ProductFunctions.ParseDays("""{"days":14}"""u8));
        Assert.NotNull(ProductFunctions.ParseDays("""{"days":"14"}"""u8).Error);
        Assert.NotNull(ProductFunctions.ParseDays("""{"days":1.5}"""u8).Error);
        Assert.NotNull(ProductFunctions.ParseDays("nope"u8).Error);
    }

    [Fact]
    public void Edit_and_shelf_life_responses()
    {
        var (status, body) = ProductFunctions.ToResponse(new EditProductDetailsResult.Saved(Id, "Semi-skimmed milk", "Dairy Manor", null));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """{"productId":"22222222-2222-2222-2222-222222222222","displayName":"Semi-skimmed milk","brand":"Dairy Manor","size":null}""",
            JsonSerializer.Serialize(body, WebJson));
        Assert.Equal(HttpStatusCode.Conflict, ProductFunctions.ToResponse(new EditProductDetailsResult.Conflict(Id, "x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, ProductFunctions.ToResponse(new EditProductDetailsResult.NotFound(Id)).Status);
        Assert.Equal(HttpStatusCode.BadRequest,
            ProductFunctions.ToResponse(new EditProductDetailsResult.Invalid(new Dictionary<string, string> { ["name"] = "Enter a name." })).Status);

        var (shelfStatus, shelfBody) = ProductFunctions.ToResponse(new SetShelfLifeResult.Saved(Id, 14));
        Assert.Equal(HttpStatusCode.OK, shelfStatus);
        Assert.Equal("""{"productId":"22222222-2222-2222-2222-222222222222","shelfLifeDays":14,"shelfLifeSource":"manual"}""",
            JsonSerializer.Serialize(shelfBody, WebJson));
        Assert.Equal(HttpStatusCode.BadRequest, ProductFunctions.ToResponse(new SetShelfLifeResult.Invalid("x")).Status);
        Assert.Equal(HttpStatusCode.Conflict, ProductFunctions.ToResponse(new SetShelfLifeResult.Conflict(Id, "x")).Status);
        Assert.Equal(HttpStatusCode.NotFound, ProductFunctions.ToResponse(new SetShelfLifeResult.NotFound(Id)).Status);
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
