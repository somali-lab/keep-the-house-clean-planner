using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// The booked redemptions in the points ledger (collection <c>pointEntries</c>, entries of kind <c>redemption</c>; ADR-0011, requirements 4.12).
/// The writes only run inside <see cref="ForRunningTransactions"/>, together with their audit entry; called outside a transaction they write
/// nothing and return a <see cref="PortError"/> whose message starts with <c>redemptions.no_transaction</c>. Reads join the running transaction.
/// </summary>
/// <remarks>
/// Snapshot isolation is not serialisable: two bookings of one person that both read the balance before either wrote would overdraw it. Every
/// booking therefore first writes the person's guard (<see cref="LockBalanceAsync"/>), a document only bookings of that person write, so that two
/// of them conflict on it and one retries and reads the other's entry (mongodb-persistence rule 8, ADR-0021).
/// </remarks>
public interface ForStoringRedemptions
{
    /// <summary>The redemption holding this request key; <see cref="NotFound"/> when there is none.</summary>
    Task<OneOf<PointEntry, NotFound, PortError>> FindByRequestIdAsync(string requestId, CancellationToken cancellationToken);

    /// <summary>The redemption with this id; <see cref="NotFound"/> for an unknown id and for an entry of another kind.</summary>
    Task<OneOf<PointEntry, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>The number of redemptions in the ledger.</summary>
    Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken);

    /// <summary>The balance of one person over the whole ledger: the sum of every entry, redemptions included.</summary>
    Task<OneOf<long, PortError>> BalanceOfAsync(string personId, CancellationToken cancellationToken);

    /// <summary>Writes the guard of the person's balance (a counter), so that two bookings of one person in flight at once conflict. Call it before reading the balance.</summary>
    Task<OneOf<Success, PortError>> LockBalanceAsync(string personId, CancellationToken cancellationToken);

    /// <summary>Inserts the booking, source <c>live</c>, with a store-assigned id and key. A request key that is already taken is <see cref="RequestKeyTaken"/>.</summary>
    Task<OneOf<PointEntry, RequestKeyTaken, PortError>> InsertAsync(NewRedemption draft, CancellationToken cancellationToken);

    /// <summary>Removes the redemption and returns it as it was stored; <see cref="NotFound"/> when it is already gone.</summary>
    Task<OneOf<PointEntry, NotFound, PortError>> DeleteAsync(string id, CancellationToken cancellationToken);
}
