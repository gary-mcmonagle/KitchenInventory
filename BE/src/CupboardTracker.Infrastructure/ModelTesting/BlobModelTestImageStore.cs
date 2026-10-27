using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Infrastructure.ModelTesting;

/// <summary>
/// The model test suite's pack image (#121) in the API app's own storage account (managed identity, Storage Blob Data
/// Owner): container <c>model-tests</c>, blob <c>pack.png</c>. Made from <see cref="TestPackImage"/> the first time it's
/// missing; after that whatever is in the blob is used, so Gary can replace it. Locally, the AzureWebJobsStorage
/// connection string (e.g. Azurite).
/// </summary>
public sealed partial class BlobModelTestImageStore(IConfiguration config, TokenCredential credential, ILogger<BlobModelTestImageStore> logger)
    : IModelTestImageStore
{
    public const string ContainerName = "model-tests";
    public const string BlobName = "pack.png";

    public async Task<byte[]> GetAsync(CancellationToken cancellationToken)
    {
        var container = config["AzureWebJobsStorage:accountName"] is { Length: > 0 } account
            ? new BlobContainerClient(new Uri($"https://{account}.blob.core.windows.net/{ContainerName}"), credential)
            : new BlobContainerClient(config["AzureWebJobsStorage"] ?? throw new InvalidOperationException("No AzureWebJobsStorage setting."), ContainerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var blob = container.GetBlobClient(BlobName);

        async Task<byte[]?> Read()
        {
            try
            {
                return (await blob.DownloadContentAsync(cancellationToken)).Value.Content.ToArray();
            }
            catch (RequestFailedException e) when (e.Status == 404)
            {
                return null;
            }
        }

        async Task<byte[]?> TryCreate(byte[] image)
        {
            try
            {
                await blob.UploadAsync(new BinaryData(image), new Azure.Storage.Blobs.Models.BlobUploadOptions
                {
                    HttpHeaders = new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = "image/png" },
                    Conditions = new Azure.Storage.Blobs.Models.BlobRequestConditions { IfNoneMatch = ETag.All },
                }, cancellationToken);
                LogCreated(image.Length);
                return image;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412)
            {
                return null; // another run made it a moment ago
            }
        }

        return await TestImageSource.GetOrCreateAsync(Read, TryCreate, Read);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Made the model test image ({Bytes} bytes) in blob storage.")]
    private partial void LogCreated(int bytes);
}

/// <summary>The "use what's there, else make it once" rule for the test image, apart from the storage it lives in.</summary>
public static class TestImageSource
{
    /// <summary>The stored image if there is one; otherwise a new <see cref="TestPackImage"/> that <paramref name="tryCreate"/>
    /// stores (null if someone else got there first, then <paramref name="readAgain"/> returns theirs).</summary>
    public static async Task<byte[]> GetOrCreateAsync(
        Func<Task<byte[]?>> read, Func<byte[], Task<byte[]?>> tryCreate, Func<Task<byte[]?>> readAgain)
    {
        if (await read() is { } existing)
        {
            return existing;
        }

        var image = TestPackImage.Create();
        return await tryCreate(image) ?? await readAgain() ?? image;
    }
}

/// <summary>When a run starts (#121): a <c>{ runId }</c> message on the <c>model-test-runs</c> queue, run by the queue-triggered
/// function.</summary>
public sealed class StorageQueueModelTestScheduler(IConfiguration config, TokenCredential credential) : Application.AiModels.IModelTestScheduler
{
    public const string QueueName = "model-test-runs";

    private readonly Lazy<Task<Azure.Storage.Queues.QueueClient>> client = Push.PushQueues.Client(config, credential, QueueName);

    public async Task EnqueueAsync(Guid runId, CancellationToken cancellationToken)
    {
        var queue = await client.Value;
        await queue.SendMessageAsync(Serialize(runId), cancellationToken: cancellationToken);
    }

    public static string Serialize(Guid runId) => System.Text.Json.JsonSerializer.Serialize(new Message(runId), Push.StorageQueuePushJobScheduler.Json);

    public static Guid? Deserialize(string message) =>
        System.Text.Json.JsonSerializer.Deserialize<Message>(message, Push.StorageQueuePushJobScheduler.Json) is { RunId: var id } && id != Guid.Empty ? id : null;

    private sealed record Message(Guid RunId);
}
