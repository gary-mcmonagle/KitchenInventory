using System.Text.Json;
using Azure.Core;
using Azure.Storage.Queues;
using CupboardTracker.Application.ShoppingList;
using CupboardTracker.Infrastructure.Push;
using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure.ShoppingList;

/// <summary>
/// A typed list item's aisle (#84): an <c>{ itemId }</c> message on the <c>list-aisles</c> queue in the API app's storage
/// account (created on first use), run straight away by the queue trigger, so the add never waits for Jev.
/// </summary>
public sealed class StorageQueueListAisleScheduler(IConfiguration config, TokenCredential credential) : IListAisleScheduler
{
    public const string QueueName = "list-aisles";

    private readonly Lazy<Task<QueueClient>> client = PushQueues.Client(config, credential, QueueName);

    public async Task ScheduleAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var queue = await client.Value;
        await queue.SendMessageAsync(Serialize(itemId), cancellationToken);
    }

    public static string Serialize(Guid itemId) => JsonSerializer.Serialize(new Message(itemId), StorageQueuePushJobScheduler.Json);

    public static Guid? Deserialize(string message) =>
        JsonSerializer.Deserialize<Message>(message, StorageQueuePushJobScheduler.Json) is { ItemId: var id } && id != Guid.Empty ? id : null;

    private sealed record Message(Guid ItemId);
}
