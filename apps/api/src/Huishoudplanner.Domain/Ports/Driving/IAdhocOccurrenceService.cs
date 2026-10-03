using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// Extra executions, one-off tasks and the retract of recorded work (requirements 4.4, ADR-0009). Node's <c>POST /occurrences</c>,
/// <c>POST /occurrences/one-off</c> and <c>POST /occurrences/:id/retract</c> are <see cref="CreateExtraAsync"/>, <see cref="CreateOneOffAsync"/>
/// and <see cref="RetractAsync"/> here. Every write is one transaction with its audit entries. A request key (<c>requestId</c>) makes a
/// creation idempotent: the same key for the same request returns the stored record (<see cref="AdhocResult.Created"/> false, nothing written),
/// the same key for another request is a <see cref="ConflictError"/> <c>idempotency_key_conflict</c>, and of concurrent requests with one key
/// exactly one creates. A day outside the generated cycles is <c>cycle_not_generated</c> (extension <c>date</c>). Who may call what is decided
/// by the driving adapter; the <see cref="Actor"/> is attribution and the default person of recorded work.
/// </summary>
public interface IAdhocOccurrenceService
{
    /// <summary>
    /// Plans an extra execution of an active task on a generated day, or records it as already done today (<c>done</c>: only for today and for
    /// a person, <c>done_requires_today</c>, <c>done_requires_person</c>). Warns <c>task_already_planned</c> when the task has an open occurrence
    /// that day. A recorded execution refreshes the task's <c>lastCompletedAt</c>.
    /// </summary>
    Task<OneOf<AdhocResult, ValidationErrors, ConflictError, SettingsMissing, PortError>> CreateExtraAsync(Actor actor, ExtraExecutionCommand command, CancellationToken cancellationToken);

    /// <summary>A one-off task: an ad-hoc occurrence without a task record (name, duration, optional active room and points live on the occurrence). Same rules as an extra execution.</summary>
    Task<OneOf<AdhocResult, ValidationErrors, ConflictError, SettingsMissing, PortError>> CreateOneOffAsync(Actor actor, OneOffCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Undoes recorded work of today: the occurrence is deleted (audited with the reason <c>retract</c>) and the task's <c>lastCompletedAt</c> falls
    /// back. Anything but recorded extra work is <c>not_retractable</c>, an earlier day <c>retract_not_today</c>, and a second retract is <see cref="NotFound"/>.
    /// </summary>
    Task<OneOf<Success, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> RetractAsync(Actor actor, string id, CancellationToken cancellationToken);
}
