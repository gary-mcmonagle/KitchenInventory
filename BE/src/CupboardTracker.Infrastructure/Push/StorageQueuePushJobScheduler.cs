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

/// <summary>
/// When to run a list push (#86): messages on the <c>list-push</c> queue, hidden until due. <c>{ kind: "batch", batchId }</c>
/// for the household batch (each add sends one; the job sends only when the batch is due and open), and
/// <c>{ kind: "followUp", personId, dueAt }</c> for one person's send held back by the minimum gap.
/// </summary>
public sealed class StorageQueueListPushScheduler(IConfiguration config, TokenCredential credential) : IListPushScheduler
{
    public const string QueueName = "list-push";

    public const string Batch = "batch";
    public const string FollowUp = "followUp";

    private readonly Lazy<Task<QueueClient>> client = PushQueues.Client(config, credential, QueueName);

    public Task ScheduleBatchAsync(Guid batchId, TimeSpan delay, CancellationToken cancellationToken) =>
        SendAsync(new ListPushMessage(Batch, batchId, null, null), delay, cancellationToken);

    public Task ScheduleFollowUpAsync(Guid personId, DateTimeOffset dueAt, TimeSpan delay, CancellationToken cancellationToken) =>
        SendAsync(new ListPushMessage(FollowUp, null, personId, dueAt), delay, cancellationToken);

    private async Task SendAsync(ListPushMessage message, TimeSpan delay, CancellationToken cancellationToken)
    {
        var queue = await client.Value;
        await queue.SendMessageAsync(Serialize(message), visibilityTimeout: delay > TimeSpan.Zero ? delay : TimeSpan.Zero, cancellationToken: cancellationToken);
    }

    public static string Serialize(ListPushMessage message) => JsonSerializer.Serialize(message, StorageQueuePushJobScheduler.Json);

    /// <summary>Null for anything that isn't a whole batch or follow-up message.</summary>
    public static ListPushMessage? Deserialize(string message) =>
        JsonSerializer.Deserialize<ListPushMessage>(message, StorageQueuePushJobScheduler.Json) is { } m
        && (m.Kind == Batch ? m.BatchId is { } b && b != Guid.Empty
            : m.Kind == FollowUp && m.PersonId is { } p && p != Guid.Empty && m.DueAt is not null)
            ? m
            : null;
}

public sealed record ListPushMessage(string Kind, Guid? BatchId, Guid? PersonId, DateTimeOffset? DueAt);
