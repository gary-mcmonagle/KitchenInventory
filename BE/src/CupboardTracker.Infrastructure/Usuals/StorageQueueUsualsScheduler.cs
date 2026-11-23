using System.Text.Json;
using Azure.Core;
using Azure.Storage.Queues;
using CupboardTracker.Application.Usuals;
using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure.Usuals;

/// <summary>When to work out a new usuals job (#134): a <c>{ jobId }</c> message on the <c>usuals-jobs</c> queue in the API app's storage
/// account (created on first use, managed identity), run by the queue-triggered function.</summary>
public sealed class StorageQueueUsualsScheduler(IConfiguration config, TokenCredential credential) : IUsualsJobScheduler
{
    public const string QueueName = "usuals-jobs";

    private readonly Lazy<Task<QueueClient>> client = Push.PushQueues.Client(config, credential, QueueName);

    public async Task EnqueueAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var queue = await client.Value;
        await queue.SendMessageAsync(Serialize(jobId), cancellationToken: cancellationToken);
    }

    public static string Serialize(Guid jobId) => JsonSerializer.Serialize(new Message(jobId), Push.StorageQueuePushJobScheduler.Json);

    public static Guid? Deserialize(string message) =>
        JsonSerializer.Deserialize<Message>(message, Push.StorageQueuePushJobScheduler.Json) is { JobId: var id } && id != Guid.Empty ? id : null;

    private sealed record Message(Guid JobId);
}
