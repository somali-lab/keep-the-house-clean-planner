using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Rooms;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

/// <summary>
/// Stores the rooms. The writes only run inside <see cref="ForRunningTransactions"/> (together with their audit entry);
/// called outside a transaction they write nothing and return a <see cref="PortError"/> whose message starts with
/// <c>rooms.no_transaction</c>. Reads join the running transaction when there is one.
/// </summary>
public interface ForStoringRooms
{
    /// <summary>At most <paramref name="take"/> rooms after the cursor, ordered by sort order, name and id.</summary>
    Task<OneOf<IReadOnlyList<Room>, PortError>> ListAsync(bool? active, RoomCursor? after, int take, CancellationToken cancellationToken);

    /// <summary><see cref="NotFound"/> also for an id that is not a valid id.</summary>
    Task<OneOf<Room, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>The rooms that exist among these ids (a malformed or unknown id is simply absent), ordered by sort order, name and id; bounded by the ids asked for.</summary>
    Task<OneOf<IReadOnlyList<Room>, PortError>> FindManyAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken);

    /// <summary>The number of rooms, active or not.</summary>
    Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken);

    /// <summary>The room with the highest sort order, or <see cref="NotFound"/> when there is no room.</summary>
    Task<OneOf<Room, NotFound, PortError>> FindLastAsync(CancellationToken cancellationToken);

    Task<OneOf<Room, PortError>> InsertAsync(NewRoom room, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the given fields and <c>updatedAt</c>, raises the version by one in the same write, and returns the room as stored afterwards. With an
    /// <paramref name="expectedVersion"/> the write is conditional on the stored version (<see cref="PreconditionFailed"/> when it differs, nothing written).
    /// </summary>
    Task<OneOf<Room, NotFound, PortError, PreconditionFailed>> UpdateAsync(
        string id, RoomChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken, int? expectedVersion = null);

    /// <summary>Deletes the room; with an <paramref name="expectedVersion"/> the delete is conditional on the stored version (<see cref="PreconditionFailed"/> when it differs).</summary>
    Task<OneOf<Success, NotFound, PortError, PreconditionFailed>> DeleteAsync(string id, CancellationToken cancellationToken, int? expectedVersion = null);
}
