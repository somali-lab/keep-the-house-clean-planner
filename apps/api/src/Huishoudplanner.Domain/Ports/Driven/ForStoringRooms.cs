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

    /// <summary>The room with the highest sort order, or <see cref="NotFound"/> when there is no room.</summary>
    Task<OneOf<Room, NotFound, PortError>> FindLastAsync(CancellationToken cancellationToken);

    Task<OneOf<Room, PortError>> InsertAsync(NewRoom room, CancellationToken cancellationToken);

    /// <summary>Sets the given fields and <c>updatedAt</c>; returns the room as stored afterwards.</summary>
    Task<OneOf<Room, NotFound, PortError>> UpdateAsync(string id, RoomChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken);

    Task<OneOf<Success, NotFound, PortError>> DeleteAsync(string id, CancellationToken cancellationToken);
}
