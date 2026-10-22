using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CupboardTracker.Application;
using CupboardTracker.Application.Stock;
using CupboardTracker.Domain.Stock;
using Microsoft.Azure.Cosmos;

namespace CupboardTracker.Infrastructure.Cosmos;

/// <summary>Units in inventoryItems (id = check-in scan ID), partitioned by /productId (docs/data-model.md).</summary>
public sealed class CosmosUnitRepository(CosmosContainers containers) : IUnitRepository
{
    private Container Items => containers.InventoryItems;

    public async Task<bool> CreateIfNotExistsAsync(Unit unit, CancellationToken cancellationToken)
    {
        using var stream = CosmosJson.ToStream(Write(unit, null));
        using var response = await Items.CreateItemStreamAsync(
            stream, new PartitionKey(unit.ProductId.ToString("D")), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<IReadOnlyList<Versioned<Unit>>> GetInStockAsync(Guid productId, CancellationToken cancellationToken)
    {
        var documents = await CosmosJson.QueryAsync(
            Items, new QueryDefinition("SELECT * FROM c WHERE c.status = 'inStock'"),
            new PartitionKey(productId.ToString("D")), max: null, cancellationToken);
        return documents.Select(d => new Versioned<Unit>(ToUnit(d), CosmosJson.Text(d["_etag"])!)).ToList();
    }

    public async Task<IReadOnlyList<Versioned<Unit>>> GetInStockWithoutExpiryAsync(CancellationToken cancellationToken)
    {
        var documents = await CosmosJson.QueryAsync(
            Items,
            new QueryDefinition(
                "SELECT * FROM c WHERE c.status = 'inStock' AND (NOT IS_DEFINED(c.estimatedExpiryDate) OR IS_NULL(c.estimatedExpiryDate))"),
            partitionKey: null, max: null, cancellationToken);
        return documents.Select(d => new Versioned<Unit>(ToUnit(d), CosmosJson.Text(d["_etag"])!)).ToList();
    }

    public async Task<IReadOnlyList<Versioned<Unit>>> GetInStockEstimatePendingAsync(CancellationToken cancellationToken)
    {
        var documents = await CosmosJson.QueryAsync(
            Items, new QueryDefinition("SELECT * FROM c WHERE c.status = 'inStock' AND c.estimatePending = true"),
            partitionKey: null, max: null, cancellationToken);
        return documents.Select(d => new Versioned<Unit>(ToUnit(d), CosmosJson.Text(d["_etag"])!)).ToList();
    }

    public async Task<bool> TryReplaceAsync(Versioned<Unit> current, Unit updated, CancellationToken cancellationToken)
    {
        var id = updated.UnitId.ToString("D");
        var partition = new PartitionKey(updated.ProductId.ToString("D"));
        using var read = await Items.ReadItemStreamAsync(id, partition, cancellationToken: cancellationToken);
        var existing = await CosmosJson.ReadAsync(read, cancellationToken);
        using var stream = CosmosJson.ToStream(Write(updated, existing));
        using var response = await Items.ReplaceItemStreamAsync(
            stream, id, partition, new ItemRequestOptions { IfMatchEtag = current.ETag }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<Versioned<Unit>?> FindAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter("@id", unitId.ToString("D"));
        var documents = await CosmosJson.QueryAsync(Items, query, partitionKey: null, max: 1, cancellationToken);
        return documents is [var d, ..] ? new Versioned<Unit>(ToUnit(d), CosmosJson.Text(d["_etag"])!) : null;
    }

    public async Task<bool> IsCheckedOutByScanAsync(Guid productId, Guid scanId, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.checkOutScanId = @scanId")
            .WithParameter("@scanId", scanId.ToString("D"));
        using var iterator = Items.GetItemQueryIterator<int>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(productId.ToString("D")) });
        var count = 0;
        while (iterator.HasMoreResults)
        {
            count += (await iterator.ReadNextAsync(cancellationToken)).Sum();
        }

        return count > 0;
    }

    internal static JsonObject Write(Unit unit, JsonObject? existing)
    {
        var document = existing ?? new JsonObject();
        document["id"] = unit.UnitId.ToString("D");
        document["productId"] = unit.ProductId.ToString("D");
        document["barcode"] = unit.Barcode;
        document["status"] = unit.Status == UnitStatus.InStock ? "inStock" : "checkedOut";
        document["checkInScanId"] = unit.CheckInScanId?.ToString("D");
        document["checkInDeviceId"] = unit.CheckInDeviceId;
        document["checkedInAt"] = CosmosJson.Timestamp(unit.CheckedInAt);
        document["checkedOutAt"] = CosmosJson.Timestamp(unit.CheckedOutAt);
        document["checkOutScanId"] = unit.CheckOutScanId?.ToString("D");
        document["checkOutSource"] = unit.CheckOutSource;
        document["checkedOutBy"] = unit.CheckedOutBy?.ToString("D");
        if (unit.CheckedInBy is { } addedBy)
        {
            document["checkedInBy"] = addedBy.ToString("D"); // added in the app, or a store label assigned there (#58)
            document["checkInSource"] = unit.CheckInScanId is null ? "ui" : "scanner";
        }

        // Set or removed, so Undo back to "no date" (#65) never leaves an old value behind.
        Set(document, "estimatedExpiryDate", Date(unit.EstimatedExpiryDate));
        Set(document, "expirySource", unit.ExpirySource is { } source ? SourceName(source)
            : unit.EstimatedExpiryDate is not null ? "shelfLife" : null);
        Set(document, "openedOn", Date(unit.OpenedOn));
        Set(document, "frozenOn", Date(unit.FrozenOn));
        Set(document, "defrostedOn", Date(unit.DefrostedOn));
        Set(document, "currentStorage", unit.CurrentStorage is { } storage ? ProductDocument.StorageName(storage) : null);
        Set(document, "estimatePending", unit.EstimatePending ? true : null);
        Set(document, "beforeStateChange", unit.BeforeStateChange is { } before ? WriteSnapshot(before) : null);
        Set(document, "lastConfirmedAt", CosmosJson.Timestamp(unit.LastConfirmedAt));
        Set(document, "lastConfirmedVia", unit.LastConfirmedVia);
        Set(document, "checkOutReason", unit.CheckOutReason);
        return document;
    }

    private static void Set(JsonObject document, string name, JsonNode? value)
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

    private static JsonNode? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? ParseDate(JsonNode? node) =>
        DateOnly.TryParseExact(CosmosJson.Text(node), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string SourceName(ExpirySource source) => source switch
    {
        ExpirySource.Manual => "manual",
        ExpirySource.Opened => "opened",
        ExpirySource.Frozen => "frozen",
        ExpirySource.Defrosted => "defrosted",
        _ => "shelfLife",
    };

    private static ExpirySource? ParseSource(string? text, bool hasDate) => text switch
    {
        "manual" => ExpirySource.Manual,
        "shelfLife" => ExpirySource.ShelfLife,
        "opened" => ExpirySource.Opened,
        "frozen" => ExpirySource.Frozen,
        "defrosted" => ExpirySource.Defrosted,
        // Dates written before #53 have no source; they all came from the shelf life.
        _ => hasDate ? ExpirySource.ShelfLife : null,
    };

    private static JsonObject WriteSnapshot(UnitEstimateSnapshot s)
    {
        var snapshot = new JsonObject();
        Set(snapshot, "estimatedExpiryDate", Date(s.EstimatedExpiryDate));
        Set(snapshot, "expirySource", s.ExpirySource is { } source ? SourceName(source) : null);
        Set(snapshot, "openedOn", Date(s.OpenedOn));
        Set(snapshot, "frozenOn", Date(s.FrozenOn));
        Set(snapshot, "defrostedOn", Date(s.DefrostedOn));
        Set(snapshot, "currentStorage", s.CurrentStorage is { } storage ? ProductDocument.StorageName(storage) : null);
        Set(snapshot, "estimatePending", s.EstimatePending ? true : null);
        Set(snapshot, "before", s.Before is { } before ? WriteSnapshot(before) : null);
        return snapshot;
    }

    private static UnitEstimateSnapshot ReadSnapshot(JsonObject s) => new(
        ParseDate(s["estimatedExpiryDate"]),
        ParseSource(CosmosJson.Text(s["expirySource"]), s["estimatedExpiryDate"] is not null),
        ParseDate(s["openedOn"]),
        ParseDate(s["frozenOn"]),
        ParseDate(s["defrostedOn"]),
        ProductDocument.ParseStorage(CosmosJson.Text(s["currentStorage"])),
        s["estimatePending"] is JsonValue p && p.TryGetValue<bool>(out var pending) && pending,
        s["before"] is JsonObject before ? ReadSnapshot(before) : null);

    internal static Unit ToUnit(JsonObject d) => new(
        Guid.Parse(CosmosJson.Text(d["id"])!),
        Guid.Parse(CosmosJson.Text(d["productId"])!),
        CosmosJson.Text(d["barcode"]),
        CosmosJson.Text(d["status"]) == "checkedOut" ? UnitStatus.CheckedOut : UnitStatus.InStock,
        Guid.TryParse(CosmosJson.Text(d["checkInScanId"]), out var inScan) ? inScan : null,
        CosmosJson.Text(d["checkInDeviceId"]),
        CosmosJson.ParseTimestamp(d["checkedInAt"]) ?? DateTimeOffset.MinValue,
        CosmosJson.ParseTimestamp(d["checkedOutAt"]),
        Guid.TryParse(CosmosJson.Text(d["checkOutScanId"]), out var outScan) ? outScan : null,
        CosmosJson.Text(d["checkOutSource"]),
        ParseDate(d["estimatedExpiryDate"]),
        Guid.TryParse(CosmosJson.Text(d["checkedOutBy"]), out var by) ? by : null,
        ParseSource(CosmosJson.Text(d["expirySource"]), d["estimatedExpiryDate"] is not null),
        Guid.TryParse(CosmosJson.Text(d["checkedInBy"]), out var addedBy) ? addedBy : null,
        ParseDate(d["openedOn"]),
        ParseDate(d["frozenOn"]),
        ParseDate(d["defrostedOn"]),
        ProductDocument.ParseStorage(CosmosJson.Text(d["currentStorage"])),
        d["estimatePending"] is JsonValue p && p.TryGetValue<bool>(out var pending) && pending,
        d["beforeStateChange"] is JsonObject before ? ReadSnapshot(before) : null,
        CosmosJson.ParseTimestamp(d["lastConfirmedAt"]),
        CosmosJson.Text(d["lastConfirmedVia"]),
        CosmosJson.Text(d["checkOutReason"]));
}

/// <summary>Unmatched check-outs (id = scan ID), partitioned by /id (docs/data-model.md).</summary>
public sealed class CosmosUnmatchedCheckOutRepository(CosmosContainers containers) : IUnmatchedCheckOutRepository
{
    public async Task<bool> ExistsAsync(Guid scanId, CancellationToken cancellationToken)
    {
        var id = scanId.ToString("D");
        using var response = await containers.UnmatchedCheckOuts.ReadItemStreamAsync(
            id, new PartitionKey(id), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is not null;
    }

    public async Task<UnmatchedCheckOut?> GetAsync(Guid scanId, CancellationToken cancellationToken)
    {
        var id = scanId.ToString("D");
        using var response = await containers.UnmatchedCheckOuts.ReadItemStreamAsync(
            id, new PartitionKey(id), cancellationToken: cancellationToken);
        return await CosmosJson.ReadAsync(response, cancellationToken) is { } d ? ToUnmatched(d) : null;
    }

    public async Task DismissAsync(Guid scanId, Guid userId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var id = scanId.ToString("D");
        // A patch, so it can't overwrite anything else; a second dismissal just rewrites the same fields.
        using var response = await containers.UnmatchedCheckOuts.PatchItemStreamAsync(
            id, new PartitionKey(id),
            [
                PatchOperation.Set("/dismissed", true),
                PatchOperation.Set("/dismissedAt", CosmosJson.Timestamp(at)),
                PatchOperation.Set("/dismissedBy", userId.ToString("D")),
            ],
            new PatchItemRequestOptions { FilterPredicate = "FROM c WHERE NOT IS_DEFINED(c.dismissed) OR c.dismissed = false" },
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed)
        {
            return; // already dismissed: keep the first
        }

        response.EnsureSuccessStatusCode();
    }

    internal static UnmatchedCheckOut ToUnmatched(JsonObject d) => new(
        Guid.Parse(CosmosJson.Text(d["id"])!),
        CosmosJson.Text(d["barcode"]) ?? "",
        Guid.TryParse(CosmosJson.Text(d["productId"]), out var productId) ? productId : null,
        CosmosJson.Text(d["deviceId"]) ?? "",
        CosmosJson.ParseTimestamp(d["scannedAt"]) ?? DateTimeOffset.MinValue,
        CosmosJson.ParseTimestamp(d["recordedAt"]) ?? DateTimeOffset.MinValue,
        d["dismissed"] is JsonValue v && v.TryGetValue<bool>(out var dismissed) && dismissed
            ? CosmosJson.ParseTimestamp(d["dismissedAt"]) ?? DateTimeOffset.MinValue
            : null,
        Guid.TryParse(CosmosJson.Text(d["dismissedBy"]), out var by) ? by : null);

    public async Task<bool> CreateIfNotExistsAsync(UnmatchedCheckOut unmatched, CancellationToken cancellationToken)
    {
        var id = unmatched.ScanId.ToString("D");
        var document = new JsonObject
        {
            ["id"] = id,
            ["barcode"] = unmatched.Barcode,
            ["productId"] = unmatched.ProductId?.ToString("D"),
            ["deviceId"] = unmatched.DeviceId,
            ["scannedAt"] = CosmosJson.Timestamp(unmatched.ScannedAt),
            ["recordedAt"] = CosmosJson.Timestamp(unmatched.RecordedAt),
            ["dismissed"] = false,
            ["dismissedAt"] = null,
        };
        using var stream = CosmosJson.ToStream(document);
        using var response = await containers.UnmatchedCheckOuts.CreateItemStreamAsync(
            stream, new PartitionKey(id), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }
}
