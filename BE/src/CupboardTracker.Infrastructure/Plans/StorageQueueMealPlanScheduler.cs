using System.Text.Json;
using Azure.Core;
using Azure.Storage.Queues;
using CupboardTracker.Application.Plans;
using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure.Plans;

/// <summary>When to write a new plan's meals (#119): a <c>{ planId }</c> message on the <c>meal-plan-jobs</c> queue in the API app's
/// storage account (created on first use, managed identity), run by the queue-triggered function.</summary>
public sealed class StorageQueueMealPlanScheduler(IConfiguration config, TokenCredential credential) : IMealPlanScheduler
{
    public const string QueueName = "meal-plan-jobs";

    private readonly Lazy<Task<QueueClient>> client = Push.PushQueues.Client(config, credential, QueueName);

    public async Task EnqueueAsync(Guid planId, CancellationToken cancellationToken)
    {
        var queue = await client.Value;
        await queue.SendMessageAsync(Serialize(planId), cancellationToken: cancellationToken);
    }

    public static string Serialize(Guid planId) => JsonSerializer.Serialize(new Message(planId), Push.StorageQueuePushJobScheduler.Json);

    public static Guid? Deserialize(string message) =>
        JsonSerializer.Deserialize<Message>(message, Push.StorageQueuePushJobScheduler.Json) is { PlanId: var id } && id != Guid.Empty ? id : null;

    private sealed record Message(Guid PlanId);
}
