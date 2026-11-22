using CupboardTracker.Application.Products;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

public abstract record CameraScanResult
{
    /// <summary>Checked in: a unit of a named product. <paramref name="Count"/> is in stock now.</summary>
    public sealed record CheckedIn(Guid UnitId, Guid ProductId, string DisplayName, int Count) : CameraScanResult;

    /// <summary>Checked in, but it has no name yet (an unknown barcode, or a store label): saved to name later. No unit for a
    /// store label, so nothing to undo.</summary>
    public sealed record NeedsName(Guid? UnitId, Guid? ProductId, string Barcode) : CameraScanResult;

    public sealed record CheckedOut(Guid UnitId, Guid ProductId, string DisplayName, int RemainingCount) : CameraScanResult;

    /// <summary>Check-out of something not in stock (or never seen): nothing is recorded.</summary>
    public sealed record NotInStock(string Barcode) : CameraScanResult;

    public sealed record Invalid(string Error) : CameraScanResult;
}

/// <summary>
/// A barcode read by the phone camera (#133). A check-in goes through the same <see cref="ProcessScanEvent"/> as a scanner's
/// (product cache, Open Food Facts, no-match, store labels, the live feed, the stock push), with the scan id the client's
/// request id and the device <see cref="CameraScanning.Device"/>, so a retry is a no-op. A check-out takes the soonest-expiring
/// unit through <see cref="CheckOutUnit"/>, recorded with the source <see cref="CameraScanning.Source"/>; nothing in stock
/// records nothing (unlike a scanner's unmatched check-out). The log holds counts and outcomes only.
/// </summary>
public sealed partial class CameraScan(
    ProcessScanEvent process, CheckOutUnit checkOut, IProductRepository products, IUnitRepository units, TimeProvider clock,
    ILogger<CameraScan> logger)
{
    private const int MaxRounds = 5;

    public async Task<CameraScanResult> ExecuteAsync(
        Guid requestId, string? barcodeText, string? role, CurrentUser user, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            return new CameraScanResult.Invalid("requestId is required.");
        }

        if (!Barcode.TryCreate(barcodeText, out var barcode, out var error))
        {
            return new CameraScanResult.Invalid(error!);
        }

        var result = role switch
        {
            "in" => await CheckInAsync(requestId, barcode!, user, cancellationToken),
            "out" => await CheckOutAsync(requestId, barcode!, user, cancellationToken),
            _ => new CameraScanResult.Invalid("role must be \"in\" or \"out\"."),
        };
        LogScan(role ?? "?", Outcome(result));
        return result;
    }

    private async Task<CameraScanResult> CheckInAsync(Guid requestId, Barcode barcode, CurrentUser user, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await process.ExecuteAsync(
            new ScanEvent(requestId, barcode, CameraScanning.Device, Role.In, now, now, "app"), cancellationToken);

        var found = await units.FindAsync(requestId, cancellationToken);
        if (found is null)
        {
            return new CameraScanResult.NeedsName(null, null, barcode.Value); // a store label: waits for "what is it?"
        }

        if (found.Value.CheckedInBy is null)
        {
            await units.TryReplaceAsync(found, found.Value with { CheckedInBy = user.ObjectId }, cancellationToken); // best-effort
        }

        var unit = found.Value;
        var product = await products.GetAsync(unit.ProductId, cancellationToken);
        if (product is null || product.IsUnnamed)
        {
            return new CameraScanResult.NeedsName(unit.UnitId, unit.ProductId, barcode.Value);
        }

        var count = (await units.GetInStockAsync(unit.ProductId, cancellationToken)).Count;
        return new CameraScanResult.CheckedIn(unit.UnitId, unit.ProductId, product.DisplayName, count);
    }

    private async Task<CameraScanResult> CheckOutAsync(Guid requestId, Barcode barcode, CurrentUser user, CancellationToken cancellationToken)
    {
        var productId = await products.GetProductIdForBarcodeAsync(barcode.Value, cancellationToken);
        if (productId is null)
        {
            return new CameraScanResult.NotInStock(barcode.Value);
        }

        var product = await products.GetAsync(productId.Value, cancellationToken);
        var name = product is { IsUnnamed: false } ? product.DisplayName : Domain.Products.Product.UnknownName(barcode.Value);

        // A retry of a request that did check a unit out: the same answer, nothing more taken.
        if (await units.FindCheckedOutByScanAsync(productId.Value, requestId, cancellationToken) is { } already)
        {
            return new CameraScanResult.CheckedOut(
                already.UnitId, productId.Value, name, (await units.GetInStockAsync(productId.Value, cancellationToken)).Count);
        }

        for (var round = 0; round < MaxRounds; round++)
        {
            var inStock = await units.GetInStockAsync(productId.Value, cancellationToken);
            if (inStock.Count == 0)
            {
                return new CameraScanResult.NotInStock(barcode.Value);
            }

            foreach (var candidate in UnitSelection.InCheckOutOrder(inStock.Select(v => v.Value)))
            {
                var taken = await checkOut.ExecuteAsync(
                    candidate.UnitId, user, cancellationToken, scanId: requestId, source: CameraScanning.Source);
                if (taken is CheckOutUnitResult.CheckedOut c)
                {
                    return new CameraScanResult.CheckedOut(c.UnitId, c.ProductId, name, c.RemainingCount);
                }
            }
        }

        throw new InvalidOperationException($"Couldn't check out a unit for camera scan {requestId} after {MaxRounds} rounds of conflicts.");
    }

    private static string Outcome(CameraScanResult result) => result switch
    {
        CameraScanResult.CheckedIn => "checkedIn",
        CameraScanResult.NeedsName => "needsName",
        CameraScanResult.CheckedOut => "checkedOut",
        CameraScanResult.NotInStock => "notInStock",
        _ => "invalid",
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Camera scan ({Role}): {Outcome}.")]
    private partial void LogScan(string role, string outcome);
}
