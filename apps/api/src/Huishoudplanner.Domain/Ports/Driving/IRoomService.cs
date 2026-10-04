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

    /// <summary>One room; <see cref="NotFound"/> for an unknown id, a <see cref="ValidationErrors"/> on <c>id</c> for a malformed one.</summary>
    Task<OneOf<Room, NotFound, ValidationErrors, PortError>> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>Creates an active room. Without a sort order it goes ten places after the last room (10 for the first).</summary>
    Task<OneOf<Room, ValidationErrors, ConflictError, PortError>> CreateAsync(Actor actor, CreateRoomCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the given fields. A patch that changes nothing writes and audits nothing and returns the room as it is. <paramref name="expectedVersion"/> is the
    /// version the caller read (<c>If-Match</c>, ADR-0022): another version is a <see cref="PreconditionFailed"/>, also for a patch that would change nothing;
    /// <see langword="null"/> skips the check.
    /// </summary>
    Task<OneOf<Room, NotFound, ValidationErrors, ConflictError, PortError, PreconditionFailed>> UpdateAsync(
        Actor actor, string id, RoomPatch patch, CancellationToken cancellationToken, int? expectedVersion = null);

    /// <summary>
    /// Deletes a room that no task uses; otherwise <see cref="RoomInUse"/> (<c>409 room_in_use</c>). Another <paramref name="expectedVersion"/> than the stored one
    /// is a <see cref="PreconditionFailed"/>.
    /// </summary>
    Task<OneOf<Success, NotFound, ValidationErrors, RoomInUse, ConflictError, PortError, PreconditionFailed>> DeleteAsync(
        Actor actor, string id, CancellationToken cancellationToken, int? expectedVersion = null);
}
