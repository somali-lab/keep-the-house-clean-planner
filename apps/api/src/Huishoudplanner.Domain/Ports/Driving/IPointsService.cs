using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The points ledger as the API sees it (requirements 4.12, ADR-0011): the balances and the entries of a person, and the reconciliation that makes the
/// ledger match the occurrences. The other kinds of entries (bonuses, redemptions) are read like any other; their writers join in slices 4.2 and 4.3.
/// </summary>
public interface IPointsService
{
    /// <summary>The balance of every active person and of every inactive person with entries in the range, both days included; the whole ledger without a range.</summary>
    Task<OneOf<PointsBalances, ValidationErrors, SettingsMissing, PortError>> BalancesAsync(DateOnly? fromDay, DateOnly? toDay, CancellationToken cancellationToken);

    /// <summary>One page of the entries of one person in the range, newest date first, then by id. A range longer than 371 days is <c>range_too_large</c> on <c>to</c>.</summary>
    Task<OneOf<PointEntryList, ValidationErrors, SettingsMissing, PortError>> EntriesAsync(PointEntriesRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the whole ledger match the occurrences, idempotently, in one transaction. A run that changes something records one summary entry
    /// (<c>points</c> / <c>recompute</c>) attributed to <paramref name="actor"/> and nothing per entry; a run that changes nothing writes and audits
    /// nothing. Without settings there is nothing to reconcile and the result is empty. Runs never overlap in this process.
    /// </summary>
    Task<OneOf<PointsRecomputeResult, ConflictError, PortError>> RecomputeAsync(AuditActor actor, PointsRecomputeTrigger trigger, CancellationToken cancellationToken);
}
