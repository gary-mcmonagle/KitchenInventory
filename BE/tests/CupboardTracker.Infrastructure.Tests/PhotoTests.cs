using System.Net;
using System.Text.Json.Nodes;
using Azure.Core;
using CupboardTracker.Application.Adding;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using CupboardTracker.Infrastructure.Cosmos;
using CupboardTracker.Infrastructure.Photos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CupboardTracker.Infrastructure.Tests;

public sealed class FoundryPhotoRecogniserTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private static readonly PhotoRecognitionOptions Options =
        new(new Uri("https://ais.example.test/"), "gpt-4o") { FirstRetryDelay = TimeSpan.Zero };

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token-not-real", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.Equal(["https://cognitiveservices.azure.com/.default"], requestContext.Scopes);
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    private sealed class Handler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int next;

        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return responses[Math.Min(next++, responses.Length - 1)]();
        }
    }

    private static HttpResponseMessage Status(int status, string body = "{}") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string Answer(string content) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }),
        ["usage"] = new JsonObject { ["total_tokens"] = 1234 },
    }.ToJsonString();

    private static (FoundryPhotoRecogniser Recogniser, Handler Handler) Make(PhotoRecognitionOptions options, params Func<HttpResponseMessage>[] responses)
    {
        var handler = new Handler(responses);
        return (new FoundryPhotoRecogniser(new HttpClient(handler), options, new Credential(), TimeProvider.System,
            NullLogger<FoundryPhotoRecogniser>.Instance), handler);
    }

    [Fact]
    public async Task Sends_the_photo_with_the_prompt_and_strict_schema_using_the_managed_identity_token()
    {
        var (recogniser, handler) = Make(Options, () => Status(200, Answer("""{"readable":true,"items":[]}""")));

        Assert.IsType<PhotoRecognitionResult.Recognised>(await recogniser.RecogniseAsync(Jpeg, CancellationToken.None));

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://ais.example.test/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21", request.RequestUri!.ToString());
        Assert.Equal(("Bearer", "test-token-not-real"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        var json = JsonNode.Parse(body)!;
        Assert.Equal(FoundryPhotoRecogniser.Instructions, json["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(Jpeg),
            json["messages"]![1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.True(json["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
    }

    [Fact]
    public void The_answer_is_cleaned_up()
    {
        var content = """
            {"readable":true,"items":[
              {"name":" Milk chocolate fruit and nut bar ","brand":"Cadbury","size":"180g","kind":"packaged","quantity":1},
              {"name":"Leeks","brand":"null","size":"unknown","kind":"loose","quantity":40},
              {"name":"Ham","brand":"","size":null,"kind":"storeLabelled","quantity":0},
              {"name":"Kitchen roll","brand":null,"size":null,"kind":"notFood","quantity":1},
              {"name":"","brand":null,"size":null,"kind":"loose","quantity":1}
            ]}
            """;

        var (recognition, tokens) = FoundryPhotoRecogniser.Parse(Answer(content));

        Assert.Equal(1234, tokens);
        Assert.Equal(
            [
                new RecognisedItem("Milk chocolate fruit and nut bar", "Cadbury", "180g", ItemKind.Packaged, 1),
                new RecognisedItem("Leeks", null, null, ItemKind.Loose, 20),
                new RecognisedItem("Ham", null, null, ItemKind.StoreLabelled, 1),
            ],
            recognition.Items);
    }

    [Fact]
    public void An_unreadable_photo_says_so() =>
        Assert.False(FoundryPhotoRecogniser.Parse(Answer("""{"readable":false,"items":[]}""")).Recognition.Readable);

    [Fact]
    public async Task Throttling_and_server_errors_are_retried_then_reported_unavailable()
    {
        var (recogniser, handler) = Make(Options, () => Status(429), () => Status(500), () => Status(503));

        var result = Assert.IsType<PhotoRecognitionResult.Unavailable>(await recogniser.RecogniseAsync(Jpeg, CancellationToken.None));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("HTTP 503 (after 3 attempts)", result.Reason);
    }

    [Fact]
    public async Task A_retry_that_succeeds_is_recognised()
    {
        var (recogniser, _) = Make(Options, () => Status(429), () => Status(200, Answer("""{"readable":true,"items":[]}""")));

        Assert.IsType<PhotoRecognitionResult.Recognised>(await recogniser.RecogniseAsync(Jpeg, CancellationToken.None));
    }

    [Fact]
    public async Task A_refusal_isnt_retried_and_no_endpoint_means_unavailable()
    {
        var (recogniser, handler) = Make(Options, () => Status(403));
        Assert.Equal("HTTP 403", Assert.IsType<PhotoRecognitionResult.Unavailable>(await recogniser.RecogniseAsync(Jpeg, CancellationToken.None)).Reason);
        Assert.Single(handler.Requests);

        var (unconfigured, none) = Make(Options with { Endpoint = null }, () => Status(200));
        Assert.IsType<PhotoRecognitionResult.Unavailable>(await unconfigured.RecogniseAsync(Jpeg, CancellationToken.None));
        Assert.Empty(none.Requests);
    }

    [Fact]
    public void Options_come_from_app_settings()
    {
        var options = PhotoRecognitionOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI_SERVICES_ENDPOINT"] = "https://ais-cupboard.cognitiveservices.azure.com/",
        }).Build());

        Assert.Equal(("https://ais-cupboard.cognitiveservices.azure.com/", "gpt-4o"), (options.Endpoint!.ToString(), options.Deployment));
    }
}

/// <summary>#58's stored fields survive a write and a read.</summary>
public sealed class AddingDocumentTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

    private static JsonObject Stored(JsonObject document) => JsonNode.Parse(document.ToJsonString())!.AsObject();

    [Fact]
    public void A_store_label_check_in_round_trips_before_and_after_assigning()
    {
        var checkIn = new StoreLabelCheckIn(Guid.NewGuid(), "2012345678903", "scanner-in", At.AddMinutes(-1), At);
        var resolved = checkIn.Resolved(Guid.NewGuid(), Guid.NewGuid(), At) with { FeedDismissedAt = At };

        foreach (var value in new[] { checkIn, resolved })
        {
            var document = Stored(CosmosStoreLabelRepository.Write(value));
            Assert.Equal(($"storeLabel:{value.ScanId:D}", "storeLabelCheckIn"), (document["id"]!.GetValue<string>(), document["type"]!.GetValue<string>()));
            Assert.Equal(value, CosmosStoreLabelRepository.Read(document));
        }
    }

    [Fact]
    public void A_unit_added_in_the_app_and_a_loose_product_round_trip()
    {
        var user = Guid.NewGuid();
        var unit = Unit.AddedInApp(Guid.NewGuid(), Guid.NewGuid(), user, At, new DateOnly(2026, 10, 25));
        var document = Stored(CosmosUnitRepository.Write(unit, null));
        Assert.Equal(("ui", user.ToString("D")), (document["checkInSource"]!.GetValue<string>(), document["checkedInBy"]!.GetValue<string>()));
        Assert.Equal(unit, CosmosUnitRepository.ToUnit(document));

        var product = Product.Loose(Guid.NewGuid(), "Leeks", null, null, ProductSource.Photo, user, At) with { LastAddedAt = At };
        var read = ProductDocument.ToProduct(Stored(ProductDocument.Write(product, null)));
        Assert.Equal(("Leeks", ProductSource.Photo, true, At, user), (read.DisplayName, read.Source!.Value, read.IsLoose, read.LastAddedAt!.Value, read.NamedBy!.Value));
    }
}

/// <summary>#65's stored fields survive a write and a read.</summary>
public sealed class UnitStateDocumentTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

    private static JsonObject Stored(JsonObject document) => JsonNode.Parse(document.ToJsonString())!.AsObject();

    private static Unit Jar() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "8001234567890", UnitStatus.InStock, null, null, At, null, null, null,
            new DateOnly(2027, 4, 8), null, ExpirySource.Manual);

    [Fact]
    public void An_opened_unit_with_its_undo_snapshot_round_trips_and_undo_clears_it()
    {
        var opened = Jar() with
        {
            EstimatedExpiryDate = new DateOnly(2026, 10, 22), ExpirySource = ExpirySource.Opened, OpenedOn = new DateOnly(2026, 10, 18),
            CurrentStorage = Storage.Fridge, BeforeStateChange = Jar().Snapshot(),
        };

        var document = Stored(CosmosUnitRepository.Write(opened, null));

        Assert.Equal(("opened", "2026-10-18", "fridge", "manual"),
            (document["expirySource"]!.GetValue<string>(), document["openedOn"]!.GetValue<string>(),
             document["currentStorage"]!.GetValue<string>(), document["beforeStateChange"]!["expirySource"]!.GetValue<string>()));
        Assert.Equal(opened, CosmosUnitRepository.ToUnit(document));

        var undone = Stored(CosmosUnitRepository.Write(opened.UndoStateChange(), document));
        Assert.False(undone.ContainsKey("openedOn") || undone.ContainsKey("beforeStateChange") || undone.ContainsKey("currentStorage"));
        Assert.Equal("manual", undone["expirySource"]!.GetValue<string>());
    }

    [Fact]
    public void An_undo_chain_round_trips()
    {
        var frozen = Jar() with
        {
            ExpirySource = ExpirySource.Frozen, FrozenOn = new DateOnly(2026, 10, 18), CurrentStorage = Storage.Freezer,
            BeforeStateChange = Jar().Snapshot(),
        };
        var defrosted = frozen with
        {
            ExpirySource = ExpirySource.Defrosted, DefrostedOn = new DateOnly(2026, 10, 20), CurrentStorage = Storage.Fridge,
            BeforeStateChange = frozen.Snapshot(),
        };

        var read = CosmosUnitRepository.ToUnit(Stored(CosmosUnitRepository.Write(defrosted, null)));

        Assert.Equal(defrosted, read);
        Assert.Equal(Jar().Snapshot() with { }, read.BeforeStateChange!.Before);
    }

    [Fact]
    public void A_pending_unit_with_no_date_keeps_its_source_and_a_cleared_date_is_removed()
    {
        var pending = Jar() with
        {
            EstimatedExpiryDate = null, ExpirySource = ExpirySource.Frozen, FrozenOn = new DateOnly(2026, 10, 18),
            CurrentStorage = Storage.Freezer, EstimatePending = true,
        };

        var document = Stored(CosmosUnitRepository.Write(pending, Stored(CosmosUnitRepository.Write(Jar(), null))));

        Assert.False(document.ContainsKey("estimatedExpiryDate")); // the old date isn't left behind
        Assert.True(document["estimatePending"]!.GetValue<bool>());
        Assert.Equal(pending, CosmosUnitRepository.ToUnit(document));
    }

    [Fact]
    public void Product_state_estimates_round_trip()
    {
        var product = Product.Loose(Guid.NewGuid(), "Pesto", null, null, ProductSource.Manual, Guid.NewGuid(), At)
            .WithShelfLife(new ShelfLife(180, Storage.Cupboard, 0.7, false, "jev", At)) with
        {
            StateEstimates = new ProductStateEstimates(
                StateShelfLife.Manual(5, Storage.Fridge), new StateShelfLife(90, Storage.Freezer, 0.79, false, "jev"),
                new StateShelfLife(4, Storage.Cupboard, 0.633, false, "jev"), At),
        };

        var document = Stored(ProductDocument.Write(product, null));

        Assert.Equal((5, "manual", "fridge", 90, "cupboard"),
            (document["openedShelfLifeDays"]!.GetValue<int>(), document["openedShelfLifeSource"]!.GetValue<string>(),
             document["openedStorage"]!.GetValue<string>(), document["frozenShelfLifeDays"]!.GetValue<int>(),
             document["defrostedStorage"]!.GetValue<string>()));
        Assert.Equal(product.StateEstimates, ProductDocument.ToProduct(document).StateEstimates);
        Assert.Null(ProductDocument.ToProduct(Stored(ProductDocument.Write(product with { StateEstimates = null }, null))).StateEstimates);
    }

    [Theory]
    [InlineData("opened", "2026-10-12", null, "opened 12 Oct")]
    [InlineData("defrosted", null, "2026-10-05", "defrosted 5 Oct")]
    [InlineData("frozen", "2026-10-12", null, null)]
    [InlineData("shelfLife", "2026-10-12", null, null)]
    public void The_digest_note_says_how_the_estimate_came_about(string source, string? openedOn, string? defrostedOn, string? note)
    {
        var row = new JsonObject { ["expirySource"] = source, ["openedOn"] = openedOn, ["defrostedOn"] = defrostedOn };

        Assert.Equal(note, CosmosDigestQueries.StateNote(row));
    }
}

public sealed class AisleIconDocumentTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

    private static JsonObject Stored(JsonObject document) => JsonNode.Parse(document.ToJsonString())!.AsObject();

    [Fact]
    public void Aisle_and_icon_round_trip_including_an_unsure_icon_and_let_jev_choose_removes_them()
    {
        var product = Product.Loose(Guid.NewGuid(), "Gravy", null, null, ProductSource.Manual, Guid.NewGuid(), At) with
        {
            Aisle = new ProductAisle("saucesOils", "jev", 0.6, At), Icon = new ProductIcon(null, "jev", 0.28, At),
        };

        var document = Stored(ProductDocument.Write(product, null));
        Assert.Equal(("saucesOils", "jev", false), (document["aisle"]!.GetValue<string>(), document["iconSource"]!.GetValue<string>(), document.ContainsKey("icon")));
        var read = ProductDocument.ToProduct(document);
        Assert.Equal((product.Aisle, product.Icon), (read.Aisle, read.Icon));

        var cleared = Stored(ProductDocument.Write(read.WithManualIcon(null, At), document));
        Assert.False(cleared.ContainsKey("iconSource") || cleared.ContainsKey("iconConfidence"));
        Assert.True(ProductDocument.ToProduct(cleared).NeedsClassification);
    }
}
