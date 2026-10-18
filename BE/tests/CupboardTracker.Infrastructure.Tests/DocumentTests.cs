using System.Text.Json.Nodes;
using CupboardTracker.Domain.Products;
using CupboardTracker.Domain.Stock;
using CupboardTracker.Infrastructure.Cosmos;

namespace CupboardTracker.Infrastructure.Tests;

/// <summary>Item detail's stored fields (#53) survive a write and a read.</summary>
public sealed class DocumentTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

    private static JsonObject Stored(JsonObject document) => JsonNode.Parse(document.ToJsonString())!.AsObject();

    [Theory]
    [InlineData(ExpirySource.Manual, "manual")]
    [InlineData(ExpirySource.ShelfLife, "shelfLife")]
    public void A_units_expiry_source_round_trips(ExpirySource source, string stored)
    {
        var id = Guid.NewGuid();
        var unit = new Unit(id, Guid.NewGuid(), "4056489899112", UnitStatus.InStock, id, "scanner-in", At, null, null, null,
            new DateOnly(2026, 10, 25), null, source);

        var document = Stored(CosmosUnitRepository.Write(unit, null));

        Assert.Equal(("2026-10-25", stored), (document["estimatedExpiryDate"]!.GetValue<string>(), document["expirySource"]!.GetValue<string>()));
        Assert.Equal(unit, CosmosUnitRepository.ToUnit(document));
    }

    [Fact]
    public void A_date_written_before_item_detail_reads_as_from_the_shelf_life()
    {
        var id = Guid.NewGuid();
        var unit = new Unit(id, Guid.NewGuid(), null, UnitStatus.InStock, id, "scanner-in", At, null, null, null, new DateOnly(2026, 10, 25));
        var document = Stored(CosmosUnitRepository.Write(unit, null));
        document.Remove("expirySource");

        Assert.Equal(ExpirySource.ShelfLife, CosmosUnitRepository.ToUnit(document).ExpirySource);
        var undated = Stored(CosmosUnitRepository.Write(unit with { EstimatedExpiryDate = null }, null));
        Assert.Null(CosmosUnitRepository.ToUnit(undated).ExpirySource);
        Assert.False(undated.ContainsKey("expirySource"));
    }

    [Fact]
    public void An_edited_product_and_a_manual_shelf_life_round_trip()
    {
        var id = Guid.NewGuid();
        var editor = Guid.NewGuid();
        var product = Product.Found(id, "4056489899112", new ProductDetails("SEMI-SKIMMED BRITISH MILK", null, null, null, []), At, 1)
            .EditedByUser("Semi-skimmed milk", "Dairy Manor", null, editor, At)
            .WithShelfLife(ShelfLife.Manual(14, Storage.Fridge, At));

        var document = Stored(ProductDocument.Write(product, null));

        Assert.Equal(("manual", "2026-10-18T08:00:00.000Z", editor.ToString("D"), "manual"),
            (document["source"]!.GetValue<string>(), document["editedAt"]!.GetValue<string>(), document["editedBy"]!.GetValue<string>(),
             document["shelfLifeSource"]!.GetValue<string>()));
        var read = ProductDocument.ToProduct(document);
        Assert.Equal((ProductSource.Manual, "Semi-skimmed milk", At, editor, 14, true),
            (read.Source!.Value, read.DisplayName, read.EditedAt!.Value, read.EditedBy!.Value, read.ShelfLife!.Days, read.ShelfLife.IsManual));
    }
}
