using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// The points ledger (collection <c>pointEntries</c>, shared with the Node server; ADR-0011). The writes only run inside
/// <see cref="ForRunningTransactions"/> (a live sync together with its audit entry, a reconciliation together with its summary); called outside
/// a transaction they write nothing and return a <see cref="PortError"/> whose message starts with <c>pointEntries.no_transaction</c>. Reads join
/// the running transaction when there is one. Execution entries are written one by one (live sync) or in bulk (reconciliation), bonus entries only in bulk by the reconciliation
/// (ADR-0012); the redemptions (slice 4.3) will have their own writer, and every kind is read and listed all the same.
/// </summary>
public interface ForStoringPointEntries
{
    /// <summary>The entry with this unique key; <see cref="NotFound"/> when there is none.</summary>
    Task<OneOf<PointEntry, NotFound, PortError>> FindByKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>Every entry of kind execution, the set a reconciliation compares with the done occurrences.</summary>
    Task<OneOf<IReadOnlyList<PointEntry>, PortError>> FindExecutionEntriesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The execution entries that cannot be mapped (no key, or a person that is no id): the reconciliation leaves them alone and counts them, because
    /// planning as if they were absent would insert an entry on their key again and hit the unique index on every run.
    /// </summary>
    Task<OneOf<UnreadableEntries, PortError>> FindUnreadableExecutionEntriesAsync(CancellationToken cancellationToken);

    /// <summary>Inserts the entry of one execution, with <paramref name="at"/> as both timestamps. The unique key makes a second insert a failure of the transaction.</summary>
    Task<OneOf<PointEntry, PortError>> InsertExecutionAsync(string key, ExecutionEntryFields fields, PointEntrySource source, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Sets the derived fields (one <c>$set</c> per field), the source and <c>updatedAt</c>; returns the entry as stored, <see cref="NotFound"/> when it is gone.</summary>
    Task<OneOf<PointEntry, NotFound, PortError>> UpdateExecutionAsync(PointEntry current, ExecutionEntryFields fields, PointEntrySource source, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Removes the entry; <see langword="false"/> when it was already gone.</summary>
    Task<OneOf<bool, PortError>> DeleteAsync(PointEntry current, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a reconciliation as bulk writes: created entries get source <c>backfill</c>, changed entries <c>recompute</c>. Every update and delete
    /// is a compare-and-set on the entry as it was read (person, amount and date), so an entry a live sync changed meanwhile is left alone and the
    /// next run sees it again. Only what really happened is counted.
    /// </summary>
    Task<OneOf<AppliedPointEntryChanges, PortError>> ApplyChangesAsync(PointEntryChanges changes, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Every stored week and cycle bonus entry (ADR-0012), the set the bonus step of a reconciliation compares with the expected bonuses.</summary>
    Task<OneOf<IReadOnlyList<PointEntry>, PortError>> FindBonusEntriesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies the bonus differences of a reconciliation (ADR-0012): the deletes first, then the inserts, as bulk writes. Inserted entries get source
    /// <c>recompute</c> and both timestamps <paramref name="at"/>. A delete is a compare-and-set on the entry as it was read. What was done is read back, so
    /// only the writes that happened are in the result. No per-entry audit entry: the reconciliation records one summary.
    /// </summary>
    Task<OneOf<AppliedBonusChanges, PortError>> ApplyBonusChangesAsync(BonusEntryChanges changes, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The sums per person of the entries dated in [<paramref name="from"/>, <paramref name="toExclusive"/>) (both optional: the whole ledger without a range).</summary>
    Task<OneOf<IReadOnlyList<PointTotal>, PortError>> SumByPersonAsync(DateTimeOffset? from, DateTimeOffset? toExclusive, CancellationToken cancellationToken);

    /// <summary>At most <see cref="PointEntryQuery.Take"/> entries of one person in the range, newest date first, then by id, after the cursor.</summary>
    Task<OneOf<IReadOnlyList<PointEntry>, PortError>> ListAsync(PointEntryQuery query, CancellationToken cancellationToken);
}

/// <summary>The keys of the unreadable execution entries, and how many have no readable key at all.</summary>
public sealed record UnreadableEntries(IReadOnlyList<string> Keys, int WithoutKey)
{
    public static UnreadableEntries None { get; } = new([], 0);

    public int Count => Keys.Count + WithoutKey;
}
