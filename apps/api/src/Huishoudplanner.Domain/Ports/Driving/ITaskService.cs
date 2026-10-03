using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Tasks;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The tasks of the household (requirements 4.2). Who may call what (planners write, everyone reads) is decided by the driving
/// adapter; the <see cref="Actor"/> is attribution for the audit entry.
/// </summary>
public interface ITaskService
{
    /// <summary>A page of tasks ordered by name, optionally of one room and/or only active or only inactive ones. A bad <c>roomId</c>, <c>limit</c> or cursor is a <see cref="ValidationErrors"/>.</summary>
    Task<OneOf<TaskList, ValidationErrors, PortError>> ListAsync(string? roomId, bool? active, int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>
    /// Creates an active task. Without points it gets the default for its duration. The room (active), interval and default assignee
    /// (active person) must exist: each failure is a <see cref="ValidationErrors"/> on <c>roomId</c>, <c>intervalKey</c> or <c>defaultAssigneeId</c>.
    /// </summary>
    Task<OneOf<HouseholdTask, ValidationErrors, ConflictError, PortError>> CreateAsync(Actor actor, CreateTaskCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the given fields. A change of the default assignee is audited as its own <c>assign</c> entry. A patch that changes
    /// nothing writes and audits nothing and returns the task as it is.
    /// </summary>
    Task<OneOf<HouseholdTask, NotFound, ValidationErrors, ConflictError, PortError>> UpdateAsync(Actor actor, string id, TaskPatch patch, CancellationToken cancellationToken);

    /// <summary>
    /// Deactivates every active task of the room, or gives all of them one default assignee. One audit entry per task that actually
    /// changes. Returns how many tasks changed; <see cref="NotFound"/> for an unknown room.
    /// </summary>
    Task<OneOf<int, NotFound, ValidationErrors, ConflictError, PortError>> BulkUpdateRoomAsync(Actor actor, string roomId, BulkRoomChange change, CancellationToken cancellationToken);
}
