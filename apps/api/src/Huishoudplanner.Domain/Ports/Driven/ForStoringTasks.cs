using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Tasks;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Stores the tasks and answers the questions other use cases ask about them (which intervals and rooms are in use). The writes
/// only run inside <see cref="ForRunningTransactions"/> (together with their audit entry); called outside a transaction they write
/// nothing and return a <see cref="PortError"/> whose message starts with <c>tasks.no_transaction</c>. Reads join the running
/// transaction when there is one.
/// </summary>
public interface ForStoringTasks
{
    /// <summary>At most <paramref name="take"/> tasks after the cursor, ordered by name and id.</summary>
    Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> ListAsync(string? roomId, bool? active, TaskCursor? after, int take, CancellationToken cancellationToken);

    /// <summary><see cref="NotFound"/> also for an id that is not a valid id.</summary>
    Task<OneOf<HouseholdTask, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>The tasks that exist among these ids (a malformed or unknown id is simply absent), ordered by name and id; bounded by the ids asked for.</summary>
    Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> FindManyAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken);

    /// <summary>Every active task of the room, ordered by name and id.</summary>
    Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> ListActiveInRoomAsync(string roomId, CancellationToken cancellationToken);

    Task<OneOf<HouseholdTask, PortError>> InsertAsync(NewTask task, CancellationToken cancellationToken);

    /// <summary>Sets the given fields and <c>updatedAt</c>; returns the task as stored afterwards.</summary>
    Task<OneOf<HouseholdTask, NotFound, PortError>> UpdateAsync(string id, TaskChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken);

    /// <summary>The number of tasks, active or inactive, that belong to the room (a room that tasks use cannot be deleted).</summary>
    Task<OneOf<int, PortError>> CountInRoomAsync(string roomId, CancellationToken cancellationToken);

    /// <summary>The distinct interval keys of all tasks, active or inactive (an interval that tasks use cannot be removed).</summary>
    Task<OneOf<IReadOnlyList<string>, PortError>> GetIntervalKeysInUseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets the denormalised <c>lastCompletedAt</c> (and <c>updatedAt</c>), <see langword="null"/> clears it. Written together with an audit entry by
    /// the occurrence use cases, and only when the value changes.
    /// </summary>
    Task<OneOf<Success, NotFound, PortError>> SetLastCompletedAtAsync(string id, DateTimeOffset? lastCompletedAt, DateTimeOffset updatedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Permanently removes the task; <see cref="NotFound"/> when it is gone (or the id is no id). Written together with its audit entry, and
    /// after the task has left the plans and badge rules that name it. Occurrences, ledger entries and history keep their own snapshot.
    /// </summary>
    Task<OneOf<Success, NotFound, PortError>> DeleteAsync(string id, CancellationToken cancellationToken);
}
