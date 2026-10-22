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

    public async Task<IReadOnlyList<Product>> GetNeedingClassificationAsync(int max, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.type = 'product' AND c.status = 'known' "
            + "AND (NOT IS_DEFINED(c.aisle) OR IS_NULL(c.aisle) OR NOT IS_DEFINED(c.iconSource) OR IS_NULL(c.iconSource))");
        var documents = await CosmosJson.QueryAsync(Products, query, partitionKey: null, max, cancellationToken);
        return documents.Select(ProductDocument.ToProduct).ToList();
    }

    public async Task<IReadOnlyList<Product>> GetNeedingAnimalContentAsync(int max, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.type = 'product' AND c.status = 'known' AND IS_STRING(c.aisle) "
            + "AND (NOT IS_DEFINED(c.animalContent) OR IS_NULL(c.animalContent))");
        var documents = await CosmosJson.QueryAsync(Products, query, partitionKey: null, max, cancellationToken);
        return documents.Select(ProductDocument.ToProduct).ToList();
    }

    public async Task<IReadOnlyList<Product>> GetNeedingGenericNameAsync(int max, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.type = 'product' AND c.status = 'known' AND IS_STRING(c.aisle) "
            + "AND (NOT IS_DEFINED(c.genericNameId) OR IS_NULL(c.genericNameId))");
        var documents = await CosmosJson.QueryAsync(Products, query, partitionKey: null, max, cancellationToken);
        return documents.Select(ProductDocument.ToProduct).ToList();
    }

    public async Task<IReadOnlyList<Product>> GetNeedingStateEstimatesAsync(int max, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.type = 'product' AND c.status = 'known' AND IS_NUMBER(c.shelfLifeDays) "
            + "AND (NOT IS_DEFINED(c.stateEstimatedAt) OR IS_NULL(c.stateEstimatedAt))");
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

        // Opened / frozen / defrosted (#65): flat fields per state, like the shelf life.
        var states = product.States;
        WriteState(document, "opened", states.Opened, withStorage: true);
        WriteState(document, "frozen", states.Frozen, withStorage: false);
        WriteState(document, "defrosted", states.Defrosted, withStorage: true);
        if (states.EstimatedAt is { } statesAt)
        {
            document["stateEstimatedAt"] = CosmosJson.Timestamp(statesAt);
        }

        // Aisle and icon (#71, #72): set or removed, so "Let Jev choose" clears the icon fields.
        Replace(document, "aisle", product.Aisle?.Id);
        Replace(document, "aisleSource", product.Aisle?.Source);
        Replace(document, "aisleConfidence", product.Aisle is { } aisle ? Math.Round(aisle.Confidence, 3) : null);
        Replace(document, "aisleEstimatedAt", product.Aisle is { } a ? CosmosJson.Timestamp(a.At) : null);
        Replace(document, "icon", product.Icon?.Name);
        Replace(document, "iconSource", product.Icon?.Source);
        Replace(document, "iconConfidence", product.Icon is { } icon ? Math.Round(icon.Confidence, 3) : null);
        Replace(document, "iconEstimatedAt", product.Icon is { } i ? CosmosJson.Timestamp(i.At) : null);

        // Generic name (#90).
        Replace(document, "genericName", product.Generic?.Name);
        Replace(document, "genericNameId", product.Generic?.Id);
        Replace(document, "genericNameSource", product.Generic is { } g ? GenericNames.SourceName(g.Source) : null);
        Replace(document, "genericNameConfidence", product.Generic?.Confidence is { } gc ? Math.Round(gc, 3) : null);
        Replace(document, "genericNameEstimatedAt", product.Generic is { } ga ? CosmosJson.Timestamp(ga.At) : null);

        // Animal content (#112).
        Replace(document, "animalContent", product.Animal is { } an ? AnimalContents.Id(an.Value) : null);
        Replace(document, "animalContentSource", product.Animal?.Source);
        Replace(document, "animalContentConfidence", product.Animal?.Confidence is { } ac ? Math.Round(ac, 3) : null);
        Replace(document, "animalContentAt", product.Animal is { } at ? CosmosJson.Timestamp(at.At) : null);
        return document;
    }

    private static void Replace(JsonObject document, string name, JsonNode? value)
    {
        if (value is null)
        {
            document.Remove(name);
        }
        else
        {
            document[name] = value;
        }
    }

    private static ProductAisle? ReadAisle(JsonObject d) =>
        CosmosJson.Text(d["aisle"]) is { } id
            ? new ProductAisle(id, CosmosJson.Text(d["aisleSource"]) ?? ShelfLife.JevSource,
                d["aisleConfidence"] is JsonValue c && c.TryGetValue<double>(out var conf) ? conf : 0,
                CosmosJson.ParseTimestamp(d["aisleEstimatedAt"]) ?? DateTimeOffset.MinValue)
            : null;

    private static ProductAnimalContent? ReadAnimal(JsonObject d) =>
        AnimalContents.Parse(CosmosJson.Text(d["animalContent"])) is { } value
            ? new ProductAnimalContent(value, CosmosJson.Text(d["animalContentSource"]) ?? AnimalContents.JevSource,
                d["animalContentConfidence"] is JsonValue c && c.TryGetValue<double>(out var conf) ? conf : null,
                CosmosJson.ParseTimestamp(d["animalContentAt"]) ?? DateTimeOffset.MinValue)
            : null;

    private static ProductGenericName? ReadGeneric(JsonObject d) =>
        CosmosJson.Text(d["genericNameId"]) is { } id && CosmosJson.Text(d["genericName"]) is { } name
            ? new ProductGenericName(id, name, CosmosJson.Text(d["genericNameSource"]) switch
                {
                    "jevMatch" => GenericNameSource.JevMatch,
                    "foundry" => GenericNameSource.Foundry,
                    "manual" => GenericNameSource.Manual,
                    _ => GenericNameSource.Fallback,
                },
                d["genericNameConfidence"] is JsonValue c && c.TryGetValue<double>(out var conf) ? conf : null,
                CosmosJson.ParseTimestamp(d["genericNameEstimatedAt"]) ?? DateTimeOffset.MinValue)
            : null;

    /// <summary>Present once Jev has answered (or Gary chose), even with no icon (below the confidence threshold).</summary>
    private static ProductIcon? ReadIcon(JsonObject d) =>
        CosmosJson.Text(d["iconSource"]) is { } source
            ? new ProductIcon(CosmosJson.Text(d["icon"]), source,
                d["iconConfidence"] is JsonValue c && c.TryGetValue<double>(out var conf) ? conf : 0,
                CosmosJson.ParseTimestamp(d["iconEstimatedAt"]) ?? DateTimeOffset.MinValue)
            : null;

    private static void WriteState(JsonObject document, string prefix, StateShelfLife? state, bool withStorage)
    {
        if (state is null)
        {
            return;
        }

        document[$"{prefix}ShelfLifeDays"] = state.Days;
        document[$"{prefix}ShelfLifeSource"] = state.Source;
        document[$"{prefix}ShelfLifeConfidence"] = Math.Round(state.Confidence, 3);
        document[$"{prefix}LowConfidence"] = state.LowConfidence;
        if (withStorage)
        {
            document[$"{prefix}Storage"] = StorageName(state.Storage);
        }
    }

    private static StateShelfLife? ReadState(JsonObject document, string prefix, Storage? fixedStorage)
    {
        if (document[$"{prefix}ShelfLifeDays"] is not JsonValue daysValue || !daysValue.TryGetValue<int>(out var days))
        {
            return null;
        }

        var confidence = document[$"{prefix}ShelfLifeConfidence"] is JsonValue c && c.TryGetValue<double>(out var value) ? value : 0;
        var low = document[$"{prefix}LowConfidence"] is JsonValue l && l.TryGetValue<bool>(out var flag) && flag;
        var storage = fixedStorage ?? ParseStorage(CosmosJson.Text(document[$"{prefix}Storage"])) ?? Storage.Fridge;
        return new StateShelfLife(days, storage, confidence, low, CosmosJson.Text(document[$"{prefix}ShelfLifeSource"]) ?? ShelfLife.JevSource);
    }

    private static ProductStateEstimates? ReadStates(JsonObject document)
    {
        var states = new ProductStateEstimates(
            ReadState(document, "opened", null), ReadState(document, "frozen", Storage.Freezer), ReadState(document, "defrosted", null),
            CosmosJson.ParseTimestamp(document["stateEstimatedAt"]));
        return states == ProductStateEstimates.None ? null : states;
    }

    internal static Storage? ParseStorage(string? text) => text switch
    {
        "cupboard" => Storage.Cupboard,
        "fridge" => Storage.Fridge,
        "freezer" => Storage.Freezer,
        _ => null,
    };

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
            CosmosJson.ParseTimestamp(document["lastAddedAt"]),
            ReadStates(document),
            ReadAisle(document),
            ReadIcon(document),
            ReadGeneric(document),
            ReadAnimal(document));
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
