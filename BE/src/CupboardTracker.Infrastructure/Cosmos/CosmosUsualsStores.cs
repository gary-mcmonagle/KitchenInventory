using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.Usuals;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// What "Add usuals" reads (#134): every unit checked in since the start date (never its stock status), with its product's generic name
/// (else display name), aisle and generic name id. Two reads: the units, then the products in one projection. Units whose check-in was
/// undone (#133) are left out.
/// </summary>
public sealed class CosmosUsualsHistory(CosmosContainers containers) : IUsualsHistory
{
    public async Task<IReadOnlyList<UsualsCheckIn>> GetCheckInsAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var unitRows = await CosmosJson.QueryAsync(
            containers.InventoryItems,
            new QueryDefinition("SELECT c.productId, c.checkedInAt, c.checkOutReason FROM c WHERE c.checkedInAt >= @start")
                .WithParameter("@start", CosmosJson.Timestamp(since)),
            partitionKey: null, max: null, cancellationToken, "UsualsUnits");
        if (unitRows.Count == 0)
        {
            return [];
        }

        var productRows = await CosmosJson.QueryAsync(
            containers.Products,
            new QueryDefinition("SELECT c.id, c.displayName, c.genericName, c.genericNameId, c.aisle FROM c WHERE c.type = 'product'"),
            partitionKey: null, max: null, cancellationToken, "UsualsProducts");
        var products = productRows.ToDictionary(p => CosmosJson.Text(p["id"]) ?? "");
        var result = new List<UsualsCheckIn>();
        foreach (var row in unitRows)
        {
            if (CosmosJson.Text(row["checkOutReason"]) == "undoneCheckIn"
                || CosmosJson.ParseTimestamp(row["checkedInAt"]) is not { } checkedIn
                || !Guid.TryParse(CosmosJson.Text(row["productId"]), out var productId)
                || !products.TryGetValue(productId.ToString("D"), out var product))
            {
                continue;
            }

            var name = CosmosJson.Text(product["genericName"]) ?? CosmosJson.Text(product["displayName"]);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            result.Add(new UsualsCheckIn(
                name, name.Trim(), CosmosJson.Text(product["aisle"]) ?? "other", productId, CosmosJson.Text(product["genericNameId"]), checkedIn));
        }

        return result;
    }
}

/// <summary>Usuals jobs (#134) in the short-lived <c>pushSeen</c> container (partition <c>/eventKey</c>, per-document ttl) like model test
/// runs: <c>usuals:&lt;jobId&gt;</c> holds the job as the API returns it, and expires on its own.</summary>
public sealed class CosmosUsualsJobRepository(CosmosContainers containers) : IUsualsJobRepository
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    public async Task CreateAsync(UsualsJob job, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(job));
        using var response = await containers.PushSeen.CreateItemStreamAsync(stream, new PartitionKey(Key(job.JobId)), cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<Versioned<UsualsJob>?> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var response = await containers.PushSeen.ReadItemStreamAsync(Key(jobId), new PartitionKey(Key(jobId)), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } document && FromDocument(document) is { } job
            ? new Versioned<UsualsJob>(job, CosmosJson.Text(document["_etag"])!)
            : null;
    }

    public async Task<bool> TryReplaceAsync(Versioned<UsualsJob> current, UsualsJob updated, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ToDocument(updated));
        using var response = await containers.PushSeen.ReplaceItemStreamAsync(
            stream, Key(updated.JobId), new PartitionKey(Key(updated.JobId)), new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private static string Key(Guid jobId) => $"usuals:{jobId:D}";

    internal static JsonObject ToDocument(UsualsJob job) => new()
    {
        ["id"] = Key(job.JobId),
        ["eventKey"] = Key(job.JobId),
        ["type"] = "usualsJob",
        ["jobId"] = job.JobId.ToString("D"),
        ["userId"] = job.UserId.ToString("D"),
        ["status"] = job.Status.ToString().ToLowerInvariant(),
        ["step"] = job.Step,
        ["method"] = job.Method,
        ["createdAt"] = CosmosJson.Timestamp(job.CreatedAt),
        ["items"] = new JsonArray([.. job.Items.Select(i => (JsonNode)new JsonObject
        {
            ["name"] = i.Name,
            ["genericNameId"] = i.GenericNameId,
            ["aisle"] = i.Aisle,
            ["band"] = i.Band.ToString().ToLowerInvariant(),
            ["why"] = i.Why,
            ["productId"] = i.ProductId.ToString("D"),
        })]),
        ["ttl"] = (int)Ttl.TotalSeconds,
    };

    internal static UsualsJob? FromDocument(JsonObject d)
    {
        if (!Guid.TryParse(CosmosJson.Text(d["jobId"]), out var jobId) || !Guid.TryParse(CosmosJson.Text(d["userId"]), out var userId)
            || !Enum.TryParse<UsualsStatus>(CosmosJson.Text(d["status"]), ignoreCase: true, out var status)
            || CosmosJson.ParseTimestamp(d["createdAt"]) is not { } createdAt)
        {
            return null;
        }

        var items = new List<UsualsItem>();
        foreach (var node in d["items"] as JsonArray ?? [])
        {
            if (node is JsonObject i && Enum.TryParse<UsualsBand>(CosmosJson.Text(i["band"]), ignoreCase: true, out var band)
                && Guid.TryParse(CosmosJson.Text(i["productId"]), out var productId))
            {
                items.Add(new UsualsItem(
                    CosmosJson.Text(i["name"]) ?? "", CosmosJson.Text(i["genericNameId"]), CosmosJson.Text(i["aisle"]) ?? "other", band,
                    CosmosJson.Text(i["why"]) ?? "", productId));
            }
        }

        return new UsualsJob(jobId, userId, status, CosmosJson.Text(d["step"]) ?? UsualsSteps.History, CosmosJson.Text(d["method"]), items, createdAt);
    }
}
