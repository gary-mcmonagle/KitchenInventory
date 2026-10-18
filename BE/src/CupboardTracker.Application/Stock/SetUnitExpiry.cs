using System.Globalization;
using CupboardTracker.Application.Users;
using CupboardTracker.Domain.Stock;
using Microsoft.Extensions.Logging;

namespace CupboardTracker.Application.Stock;

public abstract record SetUnitExpiryResult
{
    public sealed record Saved(Guid UnitId, Guid ProductId, DateOnly Date) : SetUnitExpiryResult;

    public sealed record Invalid(string Error) : SetUnitExpiryResult;

    public sealed record NotFound(Guid UnitId) : SetUnitExpiryResult;

    /// <summary>409: the unit isn't in stock, or changed while saving.</summary>
    public sealed record Conflict(Guid UnitId, string Reason) : SetUnitExpiryResult;
}

/// <summary>
/// Corrects one unit's estimated expiry date in item detail (#53): <c>expirySource: manual</c>, so no shelf-life fill
/// or shelf-life change ever recalculates it. Other units of the product are unchanged.
/// </summary>
public sealed partial class SetUnitExpiry(IUnitRepository units, ILogger<SetUnitExpiry> logger)
{
    public static readonly DateOnly Earliest = new(2000, 1, 1);
    public static readonly DateOnly Latest = new(2100, 12, 31);

    public async Task<SetUnitExpiryResult> ExecuteAsync(Guid unitId, string? date, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed < Earliest || parsed > Latest)
        {
            return new SetUnitExpiryResult.Invalid("Date must be a real date, YYYY-MM-DD.");
        }

        var current = await units.FindAsync(unitId, cancellationToken);
        if (current is null)
        {
            return new SetUnitExpiryResult.NotFound(unitId);
        }

        if (current.Value.Status != UnitStatus.InStock)
        {
            return new SetUnitExpiryResult.Conflict(unitId, "That unit isn't in stock any more.");
        }

        if (!await units.TryReplaceAsync(current, current.Value.WithManualExpiry(parsed), cancellationToken))
        {
            return new SetUnitExpiryResult.Conflict(unitId, "That unit changed while saving. Try again.");
        }

        LogSet(unitId, parsed, user.ObjectId);
        return new SetUnitExpiryResult.Saved(unitId, current.Value.ProductId, parsed);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Unit {UnitId} estimated expiry set to {Date} in the app by user {UserId}.")]
    private partial void LogSet(Guid unitId, DateOnly date, Guid userId);
}
