using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

/// <summary>
/// Asks whether tasks still use a room. A read port on tasks: the task domain itself arrives in a later slice, until then
/// the adapter counts the documents of the existing <c>tasks</c> collection. Joins the running transaction when there is one.
/// </summary>
public interface ForCheckingRoomUsage
{
    /// <summary>The number of tasks, active or inactive, that belong to the room.</summary>
    Task<OneOf<int, PortError>> CountTasksInRoomAsync(string roomId, CancellationToken cancellationToken);
}
