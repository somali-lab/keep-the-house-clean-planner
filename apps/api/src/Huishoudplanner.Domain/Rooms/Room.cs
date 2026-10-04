namespace Huishoudplanner.Domain.Rooms;

/// <summary>
/// A space of the house (requirements 3, <c>rooms</c>). <see cref="Virtual"/> marks the room for house-wide work that belongs to
/// no single space. <see cref="Id"/> is the 24 character hexadecimal id. <see cref="Version"/> is the optimistic concurrency version of the
/// document (<see cref="Concurrency.EntityVersion"/>, ADR-0022).
/// </summary>
public sealed record Room(
    string Id,
    string Name,
    int SortOrder,
    bool Active,
    bool Virtual,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version = 0);

/// <summary>What the caller asks for when creating a room. A missing <see cref="SortOrder"/> puts the room at the end of the list.</summary>
public sealed record CreateRoomCommand(string Name, int? SortOrder = null, bool Virtual = false);

/// <summary>A partial update: only the fields that are set change (a room has no nullable field, so <see langword="null"/> means "leave as is").</summary>
public sealed record RoomPatch(string? Name = null, int? SortOrder = null, bool? Active = null, bool? Virtual = null);

/// <summary>A room as the store is asked to create it. The store assigns the id.</summary>
public sealed record NewRoom(string Name, int SortOrder, bool Active, bool Virtual, DateTimeOffset CreatedAt);

/// <summary>
/// The fields a store write changes: exactly the ones that differ from the stored room, already normalised.
/// <c>updatedAt</c> is set by the store from the instant it is given.
/// </summary>
public sealed record RoomChanges(string? Name = null, int? SortOrder = null, bool? Active = null, bool? Virtual = null);

/// <summary>A delete is refused while tasks, active or inactive, still use the room (<c>409 room_in_use</c>).</summary>
public sealed record RoomInUse(int TaskCount);
