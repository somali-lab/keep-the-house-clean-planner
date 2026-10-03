using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Stores the occurrences (collection <c>occurrences</c>, shared with the Node server). The writes only run inside
/// <see cref="ForRunningTransactions"/> (together with their audit entries); called outside a transaction they write nothing and return a
/// <see cref="PortError"/> whose message starts with <c>occurrences.no_transaction</c>. Reads join the running transaction when there is one.
/// </summary>
/// <remarks>
/// Slice 3.1 added what generation needs; slice 3.2 adds the reads (by id, by range) and the update the occurrence actions share. Slice 3.3
/// adds the ad-hoc inserts and the request key lookup on top of the <see cref="Occurrence"/> type that already holds every stored field.
/// </remarks>
public interface ForStoringOccurrences
{
    /// <summary>
    /// Inserts generated occurrences idempotently: a draft whose slot (cycle, task, planned day) already holds a generated occurrence is
    /// skipped, enforced by the unique index <c>occurrences_generated_slot_unique</c> (an ad-hoc occurrence never occupies a slot). Returns
    /// the occurrences that were really inserted, with their new ids, in the order of <paramref name="drafts"/>.
    /// </summary>
    Task<OneOf<IReadOnlyList<Occurrence>, PortError>> InsertGeneratedAsync(IReadOnlyList<NewGeneratedOccurrence> drafts, CancellationToken cancellationToken);

    /// <summary>The generated occurrences planned for a day in [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>), in the display order (day, task name, id).</summary>
    Task<OneOf<IReadOnlyList<Occurrence>, PortError>> FindGeneratedPlannedBetweenAsync(DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken cancellationToken);

    /// <summary>
    /// The replaceable occurrences on a day in [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>): open, generated and still on their planned day
    /// (see <c>OccurrenceReconciliation.IsReplaceable</c>), in the display order.
    /// </summary>
    Task<OneOf<IReadOnlyList<Occurrence>, PortError>> FindReplaceableBetweenAsync(DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken cancellationToken);

    /// <summary>Deletes the occurrences with these ids; returns how many were deleted.</summary>
    Task<OneOf<int, PortError>> DeleteAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Points the room snapshots of the open occurrences of a task from <paramref name="from"/> on at another room, so a room move follows future
    /// work while completed history keeps its snapshot. Not audited and without a new <c>updatedAt</c>, as in the Node server. Returns the number changed.
    /// </summary>
    Task<OneOf<int, PortError>> UpdateUpcomingRoomSnapshotsAsync(string taskId, DateTimeOffset from, string roomId, string roomName, CancellationToken cancellationToken);

    /// <summary><see cref="NotFound"/> also for an id that is not a valid id.</summary>
    Task<OneOf<Occurrence, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>At most <see cref="OccurrenceQuery.Take"/> occurrences of the query, in the display order (day, task name, id), after the cursor.</summary>
    Task<OneOf<IReadOnlyList<Occurrence>, PortError>> ListAsync(OccurrenceQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the fields that differ between <paramref name="before"/> (what the use case read) and <paramref name="after"/> (one <c>$set</c> per
    /// field, plus <c>updatedAt</c>), only while the stored occurrence still matches <paramref name="guard"/>. Returns the occurrence as stored
    /// afterwards; <see cref="NotFound"/> when it no longer exists and <see cref="OccurrenceStateChanged"/> when it exists but a concurrent
    /// write changed the guarded state. Needs a transaction (the audit entry belongs to it).
    /// </summary>
    Task<OneOf<Occurrence, NotFound, OccurrenceStateChanged, PortError>> UpdateAsync(
        Occurrence before, Occurrence after, OccurrenceGuard guard, DateTimeOffset updatedAt, CancellationToken cancellationToken);

    /// <summary>The newest <c>completedAt</c> among the done occurrences of the task (the index serves it); <see cref="LatestCompletion.At"/> is <see langword="null"/> when there is none.</summary>
    Task<OneOf<LatestCompletion, PortError>> FindLatestCompletionAsync(string taskId, CancellationToken cancellationToken);

    /// <summary>The occurrence that was created with this request key (<c>requestId</c>); <see cref="NotFound"/> when no occurrence holds it.</summary>
    Task<OneOf<Occurrence, NotFound, PortError>> FindByRequestIdAsync(string requestId, CancellationToken cancellationToken);

    /// <summary>The number of open occurrences of a task on a day (planned or ad hoc), for the warning that the task is already planned there.</summary>
    Task<OneOf<int, PortError>> CountOpenOfTaskOnAsync(string taskId, DateTimeOffset day, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts an ad-hoc occurrence (an extra execution or a one-off task, ADR-0009) and returns it with its new id. A <c>requestId</c> that is
    /// already stored is <see cref="RequestKeyTaken"/> (the unique index); the transaction the insert ran in is then unusable, so the caller aborts
    /// it and starts again. Needs a transaction (the audit entry belongs to it).
    /// </summary>
    Task<OneOf<Occurrence, RequestKeyTaken, PortError>> InsertAdhocAsync(NewAdhocOccurrence draft, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes recorded work (an ad-hoc occurrence that was created done) and returns the document as it was; <see cref="NotFound"/> when no such
    /// occurrence exists (any more), also when a concurrent retract was first. Needs a transaction (the audit entry belongs to it).
    /// </summary>
    Task<OneOf<Occurrence, NotFound, PortError>> DeleteRecordedAsync(string id, CancellationToken cancellationToken);
}
