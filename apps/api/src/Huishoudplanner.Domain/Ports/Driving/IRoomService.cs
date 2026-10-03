using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Rooms;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The rooms of the house (requirements 4.1). Who may call what (administrators write, everyone reads) is decided by the
/// driving adapter; the <see cref="Actor"/> is attribution for the audit entry.
/// </summary>
public interface IRoomService
{
    /// <summary>A page of rooms ordered by sort order, optionally only active or only inactive ones. A bad <c>limit</c> or cursor is a <see cref="ValidationErrors"/>.</summary>
    Task<OneOf<RoomList, ValidationErrors, PortError>> ListAsync(bool? active, int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>Creates an active room. Without a sort order it goes ten places after the last room (10 for the first).</summary>
    Task<OneOf<Room, ValidationErrors, ConflictError, PortError>> CreateAsync(Actor actor, CreateRoomCommand command, CancellationToken cancellationToken);

    /// <summary>Changes the given fields. A patch that changes nothing writes and audits nothing and returns the room as it is.</summary>
    Task<OneOf<Room, NotFound, ValidationErrors, ConflictError, PortError>> UpdateAsync(Actor actor, string id, RoomPatch patch, CancellationToken cancellationToken);

    /// <summary>Deletes a room that no task uses; otherwise <see cref="RoomInUse"/> (<c>409 room_in_use</c>).</summary>
    Task<OneOf<Success, NotFound, ValidationErrors, RoomInUse, ConflictError, PortError>> DeleteAsync(Actor actor, string id, CancellationToken cancellationToken);
}
