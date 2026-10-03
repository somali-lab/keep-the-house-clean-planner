using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Points;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// Redemptions (requirements 4.12, ADR-0011): a person gives up points for a payout or a reward by booking a negative ledger entry. Every
/// write is one transaction with its audit entry; a refused request and a replay write and audit nothing.
/// </summary>
public interface IRedemptionService
{
    /// <summary>
    /// Books a redemption for the actor, or for another person when the actor is an administrator. Refused with <c>insufficient_balance</c> (with
    /// <c>balance</c> and <c>requested</c>) when it would make the balance of the person negative. A repeated request key replays the stored
    /// booking for the same request (<see cref="BookedRedemption.Created"/> false) and is <c>idempotency_key_conflict</c> for another.
    /// </summary>
    Task<OneOf<BookedRedemption, ValidationErrors, Forbidden, ConflictError, SettingsMissing, PortError>> BookAsync(
        Actor actor, RedemptionCommand command, CancellationToken cancellationToken);

    /// <summary>Takes a redemption back: its person on the day it was booked, an administrator at any time. Audited as a delete.</summary>
    Task<OneOf<Success, NotFound, ValidationErrors, Forbidden, ConflictError, SettingsMissing, PortError>> UndoAsync(
        Actor actor, string id, CancellationToken cancellationToken);

    /// <summary>How many redemptions exist; an import of an older file removes them (requirements 4.12).</summary>
    Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken);
}
