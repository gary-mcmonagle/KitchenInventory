using System.Text.Json;
using Azure.Core;
using Azure.Storage.Queues;
using CupboardTracker.Application.Push;
using Microsoft.Extensions.Configuration;

namespace CupboardTracker.Infrastructure.Push;

/// <summary>
/// Delayed push jobs (#77): a message on the <c>push-jobs</c> queue in the API app's own storage account, hidden for
/// the delay, then run by the queue-triggered function. Managed identity (Storage Queue Data Contributor), no keys;
/// locally, the AzureWebJobsStorage connection string (e.g. Azurite).
/// </summary>
public sealed class StorageQueuePushJobScheduler : IPushJobScheduler
{
    public const string QueueName = "push-jobs";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lazy<Task<QueueClient>> client;

    public StorageQueuePushJobScheduler(IConfiguration config, TokenCredential credential) =>
        client = PushQueues.Client(config, credential, QueueName);

    public async Task ScheduleAsync(PushJob job, TimeSpan delay, CancellationToken cancellationToken)
    {
        var queue = await client.Value;
        await queue.SendMessageAsync(Serialize(job), visibilityTimeout: delay, cancellationToken: cancellationToken);
    }

    public static string Serialize(PushJob job) => JsonSerializer.Serialize(job, Json);

    public static PushJob? Deserialize(string message) => JsonSerializer.Deserialize<PushJob>(message, Json);
}

/// <summary>Queues in the API app's own storage account, created on first use.</summary>
internal static class PushQueues
{
    public static Lazy<Task<QueueClient>> Client(IConfiguration config, TokenCredential credential, string name)
    {
        // The Functions queue trigger reads Base64 messages by default.
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        return new Lazy<Task<QueueClient>>(async () =>
        {
            var queue = config["AzureWebJobsStorage:accountName"] is { Length: > 0 } account
                ? new QueueClient(new Uri($"https://{account}.queue.core.windows.net/{name}"), credential, options)
                : new QueueClient(config["AzureWebJobsStorage"] ?? throw new InvalidOperationException("No AzureWebJobsStorage setting."), name, options);
            await queue.CreateIfNotExistsAsync();
            return queue;
        });
    }
}

/// <summary>
/// When to send a stock change batch (#80): a <c>{ batchId }</c> message on the <c>stock-push</c> queue, hidden until the
/// batch is due. Each change sends one, so a batch gets several; the job sends only when the batch is due and open.
/// </summary>
public sealed class StorageQueueStockPushScheduler(IConfiguration config, TokenCredential credential) : IStockPushScheduler
{
    public const string QueueName = "stock-push";

    private readonly Lazy<Task<QueueClient>> client = PushQueues.Client(config, credential, QueueName);

    public async Task ScheduleAsync(Guid batchId, TimeSpan delay, CancellationToken cancellationToken)
    {
        var queue = await client.Value;
        await queue.SendMessageAsync(Serialize(batchId), visibilityTimeout: delay, cancellationToken: cancellationToken);
    }

    public static string Serialize(Guid batchId) => JsonSerializer.Serialize(new Message(batchId), StorageQueuePushJobScheduler.Json);

    public static Guid? Deserialize(string message) =>
        JsonSerializer.Deserialize<Message>(message, StorageQueuePushJobScheduler.Json) is { BatchId: var id } && id != Guid.Empty ? id : null;

    private sealed record Message(Guid BatchId);
}
