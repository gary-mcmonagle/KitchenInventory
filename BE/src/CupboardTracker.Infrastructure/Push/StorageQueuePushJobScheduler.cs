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

    public StorageQueuePushJobScheduler(IConfiguration config, TokenCredential credential)
    {
        // The Functions queue trigger reads Base64 messages by default.
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        client = new Lazy<Task<QueueClient>>(async () =>
        {
            var queue = config["AzureWebJobsStorage:accountName"] is { Length: > 0 } account
                ? new QueueClient(new Uri($"https://{account}.queue.core.windows.net/{QueueName}"), credential, options)
                : new QueueClient(config["AzureWebJobsStorage"] ?? throw new InvalidOperationException("No AzureWebJobsStorage setting."), QueueName, options);
            await queue.CreateIfNotExistsAsync();
            return queue;
        });
    }

    public async Task ScheduleAsync(PushJob job, TimeSpan delay, CancellationToken cancellationToken)
    {
        var queue = await client.Value;
        await queue.SendMessageAsync(Serialize(job), visibilityTimeout: delay, cancellationToken: cancellationToken);
    }

    public static string Serialize(PushJob job) => JsonSerializer.Serialize(job, Json);

    public static PushJob? Deserialize(string message) => JsonSerializer.Deserialize<PushJob>(message, Json);
}
