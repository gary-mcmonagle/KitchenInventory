using System.Text.Json.Nodes;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Infrastructure.Cosmos;
using CupboardTracker.Infrastructure.ShoppingList;

namespace CupboardTracker.Infrastructure.Tests;

public sealed class ShoppingListStorageTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static ShoppingListItem Item(bool removed = false) => new(
        Guid.NewGuid(), "Semi-skimmed milk", "semi-skimmed milk", "2", Guid.NewGuid(), "dairyEggs", ListAisleSources.Product,
        ListItemSources.Suggestion, ListItemStatus.Done, Guid.NewGuid(), "Gary", At, Guid.NewGuid(), "Sam", At.AddHours(1),
        At.AddHours(1), 3, "c3", removed);

    [Fact]
    public void Items_are_in_the_household_partition_and_round_trip()
    {
        var item = Item();

        var document = CosmosShoppingListRepository.ToDocument(item);

        Assert.Equal(("household", "item"), (document["listId"]!.GetValue<string>(), document["type"]!.GetValue<string>()));
        Assert.Null(document["ttl"]);
        Assert.Equal(item, CosmosShoppingListRepository.FromDocument(JsonNode.Parse(document.ToJsonString())!.AsObject()));
    }

    [Fact]
    public void Tombstones_expire_after_a_week()
    {
        var document = CosmosShoppingListRepository.ToDocument(Item(removed: true));

        Assert.Equal(7 * 24 * 3600, document["ttl"]!.GetValue<int>());
        Assert.True(document["removed"]!.GetValue<bool>());
    }

    [Fact]
    public void Memory_ids_are_safe_for_any_name()
    {
        var id = CosmosShoppingListRepository.MemoryId("50/50 bread? #1");

        Assert.StartsWith("memory:", id, StringComparison.Ordinal);
        Assert.DoesNotContain('/', id);
        Assert.Equal(id, CosmosShoppingListRepository.MemoryId("50/50 bread? #1"));
    }

    [Fact]
    public void The_wire_shape_has_names_not_object_ids()
    {
        var item = Item();

        var wire = ShoppingListWire.Item(item);

        Assert.Equal(("Dairy & eggs", "done", 3L, false), (wire["aisleName"], wire["status"], wire["version"], wire["findingAisle"]));
        Assert.Equal(("Gary", "Sam"), (wire["addedByName"], wire["doneByName"]));
        Assert.DoesNotContain("addedBy", wire.Keys);
        Assert.Equal("2026-10-20T09:00:00.000Z", wire["addedAt"]);
        Assert.True((bool)ShoppingListWire.Item(item with { Aisle = null })["findingAisle"]!);
    }

    [Fact]
    public void The_aisle_queue_message_carries_only_the_item_id()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, StorageQueueListAisleScheduler.Deserialize(StorageQueueListAisleScheduler.Serialize(id)));
        Assert.Null(StorageQueueListAisleScheduler.Deserialize("{}"));
    }
}
