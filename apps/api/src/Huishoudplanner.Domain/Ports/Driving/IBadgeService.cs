using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The badges as the API sees them (requirements 4.13, ADR-0014): definitions with a rule and a picture, the derived awards and the progress of a
/// person. Who may call what (administrators write, everyone reads) is decided by the driving adapter; the <see cref="Actor"/> is attribution
/// for the audit entry. A write that committed makes the awards match again afterwards; that evaluation never fails the request (a failure is logged
/// and the next reconciliation repairs it).
/// </summary>
public interface IBadgeService
{
    /// <summary>A page of badges, oldest first, optionally only active or only inactive ones. A bad <c>limit</c> or cursor is a <see cref="ValidationErrors"/>.</summary>
    Task<OneOf<BadgeList, ValidationErrors, PortError>> ListAsync(bool? active, int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a badge. Tasks that do not exist are dropped from the rule; a rule that named tasks and names none of them that exist is a
    /// <see cref="ValidationErrors"/> on <c>rule.taskIds</c> (<c>unknown_task</c>). At most 100 badges can exist (<see cref="BadgeLimitReached"/>).
    /// </summary>
    Task<OneOf<Badge, ValidationErrors, BadgeLimitReached, ConflictError, PortError>> CreateAsync(Actor actor, CreateBadgeCommand command, CancellationToken cancellationToken);

    /// <summary>Changes the given fields. A patch that changes nothing writes and audits nothing and returns the badge as it is.</summary>
    Task<OneOf<Badge, NotFound, ValidationErrors, ConflictError, PortError>> UpdateAsync(Actor actor, string id, BadgePatch patch, CancellationToken cancellationToken);

    /// <summary>Deletes a badge; its awards are withdrawn by the evaluation that follows.</summary>
    Task<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>> DeleteAsync(Actor actor, string id, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the example badges that do not exist yet, idempotent by their stable key. The tasks of an example are found among the active tasks by
    /// name; an example without a matching task is created inactive, because a rule without tasks would count every task.
    /// </summary>
    Task<OneOf<AddedExampleBadges, BadgeLimitReached, ConflictError, PortError>> AddExamplesAsync(Actor actor, BadgeLanguage language, CancellationToken cancellationToken);

    /// <summary>A page of awards, oldest first, optionally of one person.</summary>
    Task<OneOf<BadgeAwardList, ValidationErrors, PortError>> AwardsAsync(string? personId, int? limit, string? cursor, CancellationToken cancellationToken);

    /// <summary>How far one person is towards every active badge, evaluated on the audited data at this moment (not read from the stored awards).</summary>
    Task<OneOf<BadgeProgress, ValidationErrors, PortError>> ProgressAsync(string personId, CancellationToken cancellationToken);

    /// <summary>The checked picture of a badge; <see cref="NotFound"/> for an unknown badge, an id that is no id, and a badge without a picture.</summary>
    Task<OneOf<BadgeImageData, NotFound, PortError>> ImageAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// A deleted task leaves the rules that name it (ADR-0014): its id is removed, and a rule that named tasks and now names none is deactivated,
    /// because an empty list would count every task. Each change is an audited <c>badge</c> update with <c>meta: { reason: 'task_deleted', taskId }</c>.
    /// The task delete use case calls it after the task is gone.
    /// </summary>
    Task<OneOf<Success, ValidationErrors, ConflictError, PortError>> RemoveTaskFromRulesAsync(Actor actor, string taskId, CancellationToken cancellationToken);
}
