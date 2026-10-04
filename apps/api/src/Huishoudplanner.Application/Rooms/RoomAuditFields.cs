using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Rooms;

namespace Huishoudplanner.Application.Rooms;

internal static class RoomAuditFields
{
    /// <summary>The fields an audit entry records for a room: the document without id and timestamps (as the Node server).</summary>
    public static AuditObject Of(Room room) => AuditObject.Of(
        ("name", room.Name),
        ("sortOrder", room.SortOrder),
        ("active", room.Active),
        ("virtual", room.Virtual));
}
