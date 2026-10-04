using Huishoudplanner.Domain.Rooms;

namespace Huishoudplanner.Adapters.Http.Rooms;

/// <summary>A room as the API shows it. <c>version</c> is the concurrency version of the room (the number inside its ETag).</summary>
public sealed record RoomResponse(
    string Id,
    string Name,
    int SortOrder,
    bool Active,
    bool Virtual,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    internal static RoomResponse From(Room room) =>
        new(room.Id, room.Name, room.SortOrder, room.Active, room.Virtual, room.CreatedAt, room.UpdatedAt, room.Version);
}

/// <summary>One page of rooms; <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
public sealed record RoomListResponse(IReadOnlyList<RoomResponse> Items, string? NextCursor);

/// <summary>The answer to a delete.</summary>
public sealed record RoomDeletedResponse(bool Deleted);

/// <summary>
/// The body of <c>POST /api/v2/rooms</c>. <see cref="Name"/> is trimmed and must not end up empty; without a
/// <see cref="SortOrder"/> the room goes ten places after the last room.
/// </summary>
public sealed record CreateRoomRequest(string Name, int? SortOrder, bool? Virtual);

/// <summary>The body of <c>PATCH /api/v2/rooms/{id}</c>: every field is optional, a body without fields changes nothing.</summary>
public sealed record UpdateRoomRequest(string? Name, int? SortOrder, bool? Active, bool? Virtual);
