using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.Products;
using CupboardTracker.Domain.Products;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>
/// Products (id = productId, type "product") and barcode lookups (id = "barcode:&lt;digits&gt;", type "barcodeLookup")
/// in the products container, partitioned by /id (docs/data-model.md).
/// </summary>
public sealed class CosmosProductRepository(CosmosContainers containers, TimeProvider clock) : IProductRepository
{
    private Container Products => containers.Products;

    private static string LookupId(string barcode) => $"barcode:{barcode}";

    public async Task<Guid?> GetProductIdForBarcodeAsync(string barcode, CancellationToken cancellationToken)
    {
        var id = LookupId(barcode);
        using var response = await Products.ReadItemStreamAsync(id, new PartitionKey(id), cancellationToken: cancellationToken);
        var document = await CosmosJson.ReadAsync(response, cancellationToken);
        return Guid.TryParse(CosmosJson.Text(document?["productId"]), out var productId) ? productId : null;
    }

    public async Task<Guid> ClaimBarcodeAsync(string barcode, Guid proposedProductId, CancellationToken cancellationToken)
    {
        var id = LookupId(barcode);
        var document = new JsonObject
        {
            ["id"] = id,
            ["type"] = "barcodeLookup",
            ["barcode"] = barcode,
            ["productId"] = proposedProductId.ToString("D"),
            ["createdAt"] = CosmosJson.Timestamp(clock.GetUtcNow()),
        };
        using var stream = CosmosJson.ToStream(document);
        using var response = await Products.CreateItemStreamAsync(stream, new PartitionKey(id), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return await GetProductIdForBarcodeAsync(barcode, cancellationToken)
                ?? throw new InvalidOperationException($"Barcode lookup for {barcode} conflicted but can't be read.");
        }

        response.EnsureSuccessStatusCode();
        return proposedProductId;
    }

    public async Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
        (await ReadAsync(productId, cancellationToken)) is { } document ? ProductDocument.ToProduct(document) : null;

    public async Task<Product> CreateIfNotExistsAsync(Product product, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(ProductDocument.Write(product, null));
        var id = product.ProductId.ToString("D");
        using var response = await Products.CreateItemStreamAsync(stream, new PartitionKey(id), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return await GetAsync(product.ProductId, cancellationToken)
                ?? throw new InvalidOperationException($"Product {id} conflicted but can't be read.");
        }

        response.EnsureSuccessStatusCode();
        return product;
    }

    public async Task<bool> TryReplaceLookupFailedAsync(Product updated, CancellationToken cancellationToken)
    {
        var id = updated.ProductId.ToString("D");
        var current = await ReadAsync(updated.ProductId, cancellationToken);
        if (current is null || CosmosJson.Text(current["status"]) != "lookupFailed")
        {
            return false; // known and noMatch products are final; never overwritten
        }

        var etag = CosmosJson.Text(current["_etag"]);
        using var stream = CosmosJson.ToStream(ProductDocument.Write(updated, current));
        using var response = await Products.ReplaceItemStreamAsync(
            stream, id, new PartitionKey(id), new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<bool> TryReplaceUnnamedAsync(Product updated, CancellationToken cancellationToken)
    {
        var id = updated.ProductId.ToString("D");
        var current = await ReadAsync(updated.ProductId, cancellationToken);
        if (current is null || CosmosJson.Text(current["status"]) is not ("noMatch" or "lookupFailed"))
        {
            return false; // already named or looked up: never overwritten
        }

        var etag = CosmosJson.Text(current["_etag"]);
        using var stream = CosmosJson.ToStream(ProductDocument.Write(updated, current));
        using var response = await Products.ReplaceItemStreamAsync(
            stream, id, new PartitionKey(id), new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<IReadOnlyList<Product>> ListKnownAsync(CancellationToken cancellationToken)
    {
        var rows = await CosmosJson.QueryAsync(Products,
            new QueryDefinition("SELECT * FROM c WHERE c.type = 'product' AND c.status = 'known'"),
            partitionKey: null, max: null, cancellationToken);
        return rows.Select(ProductDocument.ToProduct).ToList();
    }

    public async Task<Versioned<Product>?> GetVersionedAsync(Guid productId, CancellationToken cancellationToken) =>
        (await ReadAsync(productId, cancellationToken)) is { } document
            ? new Versioned<Product>(ProductDocument.ToProduct(document), CosmosJson.Text(document["_etag"])!)
            : null;

    public async Task<bool> TryReplaceAsync(Versioned<Product> current, Product updated, CancellationToken cancellationToken)
    {
        var id = updated.ProductId.ToString("D");
        var existing = await ReadAsync(updated.ProductId, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        using var stream = CosmosJson.ToStream(ProductDocument.Write(updated, existing));
        using var response = await Products.ReplaceItemStreamAsync(
            stream, id, new PartitionKey(id), new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<IReadOnlyList<Product>> GetNeedingShelfLifeAsync(int max, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.type = 'product' AND c.status = 'known' "
            + "AND (NOT IS_DEFINED(c.shelfLifeDays) OR IS_NULL(c.shelfLifeDays))");
        var documents = await CosmosJson.QueryAsync(Products, query, partitionKey: null, max, cancellationToken);
        return documents.Select(ProductDocument.ToProduct).ToList();
    }

    public async Task<bool> TrySetShelfLifeAsync(Product updated, CancellationToken cancellationToken)
    {
        var id = updated.ProductId.ToString("D");
        var current = await ReadAsync(updated.ProductId, cancellationToken);
        if (current is null || CosmosJson.Text(current["status"]) != "known" || current["shelfLifeDays"] is not null)
        {
            return false; // not known any more, or already estimated (never overwritten)
        }

        var etag = CosmosJson.Text(current["_etag"]);
        using var stream = CosmosJson.ToStream(ProductDocument.Write(updated, current));
        using var response = await Products.ReplaceItemStreamAsync(
            stream, id, new PartitionKey(id), new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<IReadOnlyList<Product>> GetLookupFailedAsync(int max, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.type = 'product' AND c.status = 'lookupFailed' ORDER BY c.lastLookupAttemptAt");
        var documents = await CosmosJson.QueryAsync(Products, query, partitionKey: null, max, cancellationToken);
        return documents.Select(ProductDocument.ToProduct).ToList();
    }

    private async Task<JsonObject?> ReadAsync(Guid productId, CancellationToken cancellationToken)
    {
        var id = productId.ToString("D");
        using var response = await Products.ReadItemStreamAsync(id, new PartitionKey(id), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken);
    }
}

/// <summary>Product document shape (docs/data-model.md).</summary>
internal static class ProductDocument
{
    public static JsonObject Write(Product product, JsonObject? existing)
    {
        // Merge into the stored document so fields owned by later features (naming, shelf life, icons) are kept.
        var document = existing ?? new JsonObject();
        document["id"] = product.ProductId.ToString("D");
        document["type"] = "product";
        document["status"] = Status(product.Status);
        document["source"] = product.Source is { } source ? Source(source) : null;
        document["barcodes"] = new JsonArray([.. product.Barcodes.Select(b => (JsonNode)JsonValue.Create(b)!)]);
        document["displayName"] = product.DisplayName;
        document["brand"] = product.Details?.Brand;
        document["size"] = product.Details?.Size;
        document["imageUrl"] = product.Details?.ImageUrl;
        document["categoryTags"] = new JsonArray(
            [.. (product.Details?.CategoryTags ?? []).Select(t => (JsonNode)JsonValue.Create(t)!)]);
        document["fetchedAt"] = CosmosJson.Timestamp(product.FetchedAt);
        document["lookupAttempts"] = product.LookupAttempts;
        document["lastLookupAttemptAt"] = CosmosJson.Timestamp(product.LastLookupAttemptAt);
        document["lookupFailedSince"] = CosmosJson.Timestamp(product.LookupFailedSince);
        if (product.NamedAt is { } namedAt)
        {
            document["namedAt"] = CosmosJson.Timestamp(namedAt);
            document["namedBy"] = product.NamedBy?.ToString("D");
        }

        if (product.LastAddedAt is { } lastAdded)
        {
            document["lastAddedAt"] = CosmosJson.Timestamp(lastAdded);
        }

        if (product.FeedDismissedAt is { } dismissedAt)
        {
            document["feedDismissedAt"] = CosmosJson.Timestamp(dismissedAt);
        }

        if (product.EditedAt is { } editedAt)
        {
            document["editedAt"] = CosmosJson.Timestamp(editedAt);
            document["editedBy"] = product.EditedBy?.ToString("D");
        }

        if (product.ShelfLife is { } shelfLife)
        {
            document["shelfLifeDays"] = shelfLife.Days;
            document["shelfLifeSource"] = shelfLife.Source;
            document["shelfLifeConfidence"] = Math.Round(shelfLife.Confidence, 3);
            document["jevLowConfidence"] = shelfLife.LowConfidence;
            document["storage"] = StorageName(shelfLife.UsualStorage);
            document["shelfLifeEstimatedAt"] = CosmosJson.Timestamp(shelfLife.EstimatedAt);
        }

        return document;
    }

    public static Product ToProduct(JsonObject document)
    {
        var status = CosmosJson.Text(document["status"]) switch
        {
            "known" => ProductStatus.Known,
            "noMatch" => ProductStatus.NoMatch,
            _ => ProductStatus.LookupFailed,
        };
        ProductSource? source = CosmosJson.Text(document["source"]) switch
        {
            "openFoodFacts" => ProductSource.OpenFoodFacts,
            "manual" => ProductSource.Manual,
            "photo" => ProductSource.Photo,
            _ => null,
        };
        var barcodes = document["barcodes"]?.AsArray().Select(CosmosJson.Text).OfType<string>().ToList() ?? [];
        var tags = document["categoryTags"]?.AsArray().Select(CosmosJson.Text).OfType<string>().ToList() ?? [];
        var displayName = CosmosJson.Text(document["displayName"]) ?? "";
        var details = status == ProductStatus.Known
            ? new ProductDetails(displayName, CosmosJson.Text(document["brand"]), CosmosJson.Text(document["size"]),
                CosmosJson.Text(document["imageUrl"]), tags)
            : null;
        return new Product(
            Guid.Parse(CosmosJson.Text(document["id"])!), status, source, barcodes, displayName, details,
            CosmosJson.ParseTimestamp(document["fetchedAt"]),
            document["lookupAttempts"]?.GetValue<int>() ?? 0,
            CosmosJson.ParseTimestamp(document["lastLookupAttemptAt"]),
            CosmosJson.ParseTimestamp(document["lookupFailedSince"]),
            CosmosJson.ParseTimestamp(document["namedAt"]),
            Guid.TryParse(CosmosJson.Text(document["namedBy"]), out var namedBy) ? namedBy : null,
            ReadShelfLife(document),
            CosmosJson.ParseTimestamp(document["editedAt"]),
            Guid.TryParse(CosmosJson.Text(document["editedBy"]), out var editedBy) ? editedBy : null,
            CosmosJson.ParseTimestamp(document["feedDismissedAt"]),
            CosmosJson.ParseTimestamp(document["lastAddedAt"]));
    }

    private static ShelfLife? ReadShelfLife(JsonObject document)
    {
        if (document["shelfLifeDays"] is not JsonValue daysValue || !daysValue.TryGetValue<int>(out var days))
        {
            return null;
        }

        var storage = CosmosJson.Text(document["storage"]) switch
        {
            "fridge" => Storage.Fridge,
            "freezer" => Storage.Freezer,
            _ => Storage.Cupboard,
        };
        var confidence = document["shelfLifeConfidence"] is JsonValue c && c.TryGetValue<double>(out var value) ? value : 0;
        var low = document["jevLowConfidence"] is JsonValue l && l.TryGetValue<bool>(out var flag) && flag;
        return new ShelfLife(days, storage, confidence, low, CosmosJson.Text(document["shelfLifeSource"]) ?? ShelfLife.JevSource,
            CosmosJson.ParseTimestamp(document["shelfLifeEstimatedAt"]) ?? DateTimeOffset.MinValue);
    }

    internal static string StorageName(Storage storage) => storage switch
    {
        Storage.Fridge => "fridge",
        Storage.Freezer => "freezer",
        _ => "cupboard",
    };

    private static string Status(ProductStatus status) => status switch
    {
        ProductStatus.Known => "known",
        ProductStatus.NoMatch => "noMatch",
        _ => "lookupFailed",
    };

    private static string Source(ProductSource source) => source switch
    {
        ProductSource.OpenFoodFacts => "openFoodFacts",
        ProductSource.Manual => "manual",
        _ => "photo",
    };
}
