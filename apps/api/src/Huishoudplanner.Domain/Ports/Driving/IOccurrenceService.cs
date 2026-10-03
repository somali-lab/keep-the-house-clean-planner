using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The occurrences of the household (requirements 4.4, 4.8): reading them and the actions that change what happened. The Node server's
/// <c>PATCH /occurrences/:id</c> with an <c>action</c> is one method per intent here (<c>complete</c>, <c>uncomplete</c>, <c>edit_completion</c> ->
/// <see cref="EditCompletionAsync"/>, <c>skip</c>, <c>reschedule</c>, <c>assign</c> -> <see cref="AssignAsync"/>) next to the claim. Every write is
/// one transaction with its audit entries; a status that does not allow the action, or that changed in the meantime, is a
/// <see cref="ConflictError"/> <c>invalid_transition</c> (extensions <c>status</c> and <c>action</c>), a claim of work that has an assignee
/// <c>already_claimed</c>, a day outside the generated cycles <c>cycle_not_generated</c> (extension <c>date</c>). Who may call what is decided by
/// the driving adapter; the <see cref="Actor"/> is attribution and the person a take over or a claim names.
/// </summary>
public interface IOccurrenceService
{
    /// <summary>A page of the occurrences on the days <c>From</c> to <c>To</c>. A bad id, <c>limit</c>, cursor or a range that runs backwards (<c>from_after_to</c>) is a <see cref="ValidationErrors"/>.</summary>
    Task<OneOf<OccurrenceList, ValidationErrors, SettingsMissing, PortError>> ListAsync(OccurrenceListRequest request, CancellationToken cancellationToken);

    Task<OneOf<OccurrenceView, NotFound, ValidationErrors, SettingsMissing, PortError>> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Completes an open or skipped occurrence. Work of someone else needs a <c>completedBy</c> or a take over (<c>completion_choice_required</c>,
    /// both at once is <c>completion_choice_conflict</c>); unassigned work and the actor's own default to the actor. The person credited must be an active person.
    /// </summary>
    Task<OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> CompleteAsync(Actor actor, string id, CompleteCommand command, CancellationToken cancellationToken);

    /// <summary>Undoes a completion: back to the status before it. Recorded work cannot be uncompleted (<c>retract_required</c>).</summary>
    Task<OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> UncompleteAsync(Actor actor, string id, CancellationToken cancellationToken);

    /// <summary>An administrator's correction of a done occurrence: its day, its timestamp and the person credited.</summary>
    Task<OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> EditCompletionAsync(Actor actor, string id, EditCompletionCommand command, CancellationToken cancellationToken);

    /// <summary>Skips an open occurrence, with an optional reason of at most 500 characters.</summary>
    Task<OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> SkipAsync(Actor actor, string id, string? reason, CancellationToken cancellationToken);

    /// <summary>Moves an open occurrence to a generated day; the planned day stays. A move to the same day writes nothing. Warns when the assignee is unavailable that weekday.</summary>
    Task<OneOf<OccurrenceChange, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> RescheduleAsync(Actor actor, string id, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Gives an open occurrence to an active person, or to anyone (<see langword="null"/>). Warns when the person is unavailable that weekday.</summary>
    Task<OneOf<OccurrenceChange, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> AssignAsync(Actor actor, string id, string? assigneeId, CancellationToken cancellationToken);

    /// <summary>The actor takes an unassigned open occurrence; atomic.</summary>
    Task<OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> ClaimAsync(Actor actor, string id, CancellationToken cancellationToken);

    /// <summary>An administrator's correction: deletes a done occurrence for good and lets the task's <c>lastCompletedAt</c> fall back.</summary>
    Task<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>> DeleteCompletedAsync(Actor actor, string id, CancellationToken cancellationToken);
}
