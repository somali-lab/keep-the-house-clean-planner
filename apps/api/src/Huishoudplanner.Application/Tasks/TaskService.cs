using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Concurrency;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Tasks;
using OneOf;

namespace Huishoudplanner.Application.Tasks;

/// <summary>
/// The task use cases (requirements 4.2). Every state change runs in one transaction together with its audit entries; a change that
/// changes nothing writes and audits nothing (ADR-0004). Port of <c>routes/tasks.ts</c>, <c>domain/tasks.ts</c> and <c>data/tasks.ts</c>.
/// </summary>
public sealed class TaskService(
    ForStoringTasks tasks,
    ForStoringRooms rooms,
    ForStoringUsers users,
    ForStoringSettings settings,
    ForStoringOccurrences occurrences,
    ForStoringCyclePlans plans,
    IBadgeService badges,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : ITaskService
{
    public async Task<OneOf<TaskList, ValidationErrors, PortError>> ListAsync(string? roomId, bool? active, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (roomId is not null && !TaskRules.IsId(roomId))
        {
            errors["roomId"] = ["invalid_object_id"];
        }

        var take = limit ?? TaskListQuery.DefaultLimit;
        if (take is < 1 or > TaskListQuery.MaxLimit)
        {
            errors["limit"] = ["must be a whole number from 1 to " + TaskListQuery.MaxLimit];
        }

        TaskCursor? after = null;
        if (cursor is not null)
        {
            if (TaskCursor.TryDecode(cursor, out var decoded))
            {
                after = decoded;
            }
            else
            {
                errors["cursor"] = ["invalid_cursor"];
            }
        }

        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        var found = await tasks.ListAsync(roomId, active, after, take + 1, cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<TaskList, ValidationErrors, PortError>>(
            items => items.Count > take
                ? new TaskList(items.Take(take).ToList(), TaskCursor.After(items[take - 1]).Encode())
                : new TaskList(items, null),
            error => error);
    }

    public async Task<OneOf<HouseholdTask, NotFound, ValidationErrors, PortError>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!TaskRules.IsId(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var found = await tasks.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<HouseholdTask, NotFound, ValidationErrors, PortError>>(task => task, notFound => notFound, error => error);
    }

    public async Task<OneOf<HouseholdTask, ValidationErrors, ConflictError, PortError>> CreateAsync(Actor actor, CreateTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        if (TaskRules.Validate(command) is { } invalid)
        {
            return invalid;
        }

        var task = new NewTask(
            command.Name.Trim(),
            command.RoomId,
            command.IntervalKey,
            command.DurationMinutes,
            // Omitted points default from the duration: one point per minute (ADR-0011).
            command.Points ?? TaskPoints.DefaultForDuration(command.DurationMinutes),
            command.DefaultAssigneeId,
            command.Notes,
            TaskRules.NormaliseTags(command.Tags ?? []),
            time.GetUtcNow());
        var ran = await transactions.RunAsync(ct => CreateInTransactionAsync(actor, task, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<HouseholdTask, ValidationErrors, ConflictError, PortError>>(
            outcome => outcome.Match<OneOf<HouseholdTask, ValidationErrors, ConflictError, PortError>>(created => created, errors => errors, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<HouseholdTask, ValidationErrors, PortError>>> CreateInTransactionAsync(Actor actor, NewTask task, CancellationToken ct)
    {
        var checkedReferences = await CheckReferencesAsync(task.RoomId, task.IntervalKey, task.DefaultAssigneeId, ct).ConfigureAwait(false);
        if (checkedReferences.TryPickT1(out var errors, out var rest))
        {
            return TransactionOutcome.Abort<OneOf<HouseholdTask, ValidationErrors, PortError>>(errors);
        }

        if (rest.TryPickT1(out var referenceError, out _))
        {
            return TransactionOutcome.Abort<OneOf<HouseholdTask, ValidationErrors, PortError>>(referenceError);
        }

        var inserted = await tasks.InsertAsync(task, ct).ConfigureAwait(false);
        if (inserted.TryPickT1(out var insertError, out var created))
        {
            return TransactionOutcome.Abort<OneOf<HouseholdTask, ValidationErrors, PortError>>(insertError);
        }

        var recorded = await audit.RecordAsync(TaskAudit.ForCreate(AuditActor.From(actor), created), ct).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<HouseholdTask, ValidationErrors, PortError>>(created),
            error => TransactionOutcome.Abort<OneOf<HouseholdTask, ValidationErrors, PortError>>(error));
    }

    public async Task<OneOf<HouseholdTask, NotFound, ValidationErrors, ConflictError, PortError, PreconditionFailed>> UpdateAsync(
        Actor actor, string id, TaskPatch patch, CancellationToken cancellationToken, int? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(patch);
        if (!TaskRules.IsId(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        if (TaskRules.Validate(patch) is { } invalid)
        {
            return invalid;
        }

        var normalised = patch with
        {
            Name = patch.Name?.Trim(),
            Tags = patch.Tags is null ? null : TaskRules.NormaliseTags(patch.Tags),
        };
        var ran = await transactions.RunAsync(ct => UpdateInTransactionAsync(actor, id, normalised, expectedVersion, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<HouseholdTask, NotFound, ValidationErrors, ConflictError, PortError, PreconditionFailed>>(
            outcome => outcome.Match<OneOf<HouseholdTask, NotFound, ValidationErrors, ConflictError, PortError, PreconditionFailed>>(
                task => task, notFound => notFound, errors => errors, error => error, stale => stale),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<HouseholdTask, NotFound, ValidationErrors, PortError, PreconditionFailed>>> UpdateInTransactionAsync(
        Actor actor, string id, TaskPatch patch, int? expectedVersion, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<HouseholdTask, NotFound, ValidationErrors, PortError, PreconditionFailed>> Abort(OneOf<HouseholdTask, NotFound, ValidationErrors, PortError, PreconditionFailed> value) =>
            TransactionOutcome.Abort(value);

        // As in the Node server, the references are checked before the task is looked up.
        var checkedReferences = await CheckReferencesAsync(patch.RoomId, patch.IntervalKey, patch.DefaultAssignee?.UserId, ct).ConfigureAwait(false);
        if (checkedReferences.TryPickT1(out var errors, out var rest))
        {
            return Abort(errors);
        }

        if (rest.TryPickT1(out var referenceError, out _))
        {
            return Abort(referenceError);
        }

        var found = await tasks.FindAsync(id, ct).ConfigureAwait(false);
        if (found.TryPickT1(out var notFound, out var foundRest))
        {
            return Abort(notFound);
        }

        if (foundRest.TryPickT1(out var findError, out var before))
        {
            return Abort(findError);
        }

        // The precondition is checked before the no-op rule: a stale If-Match on a patch that changes nothing is still a 412.
        if (EntityVersion.Check(expectedVersion, before.Version) is { } stale)
        {
            return Abort(stale);
        }

        var after = Apply(before, patch);
        var changes = ChangeSet.Between(TaskAudit.Fields(before), TaskAudit.Fields(after));
        if (changes.IsNoOp)
        {
            return TransactionOutcome.Commit<OneOf<HouseholdTask, NotFound, ValidationErrors, PortError, PreconditionFailed>>(before);
        }

        var updated = await tasks.UpdateAsync(id, ChangesBetween(before, after), time.GetUtcNow(), ct, expectedVersion).ConfigureAwait(false);
        if (!updated.TryPickT0(out var task, out var updateFailure))
        {
            return Abort(updateFailure.Match<OneOf<HouseholdTask, NotFound, ValidationErrors, PortError, PreconditionFailed>>(
                notFound => notFound, error => error, failed => failed));
        }

        var recorded = await RecordAsync(TaskAudit.ForChange(AuditActor.From(actor), id, changes), ct).ConfigureAwait(false);
        if (recorded.TryPickT1(out var auditError, out _))
        {
            return Abort(auditError);
        }

        if (before.RoomId != task.RoomId)
        {
            var refreshed = await RefreshRoomSnapshotsAsync(task, ct).ConfigureAwait(false);
            if (refreshed.TryPickT1(out var refreshError, out _))
            {
                return Abort(refreshError);
            }
        }

        return TransactionOutcome.Commit<OneOf<HouseholdTask, NotFound, ValidationErrors, PortError, PreconditionFailed>>(task);
    }

    /// <summary>
    /// A room move follows the future open work of the task while completed history keeps its snapshot (<c>updateUpcomingOccurrenceRoomSnapshots</c>):
    /// from the start of today in the household timezone on. Not audited and without a new <c>updatedAt</c>, as in the Node server. A room or
    /// settings document that is missing skips the refresh instead of failing the task update.
    /// </summary>
    private async Task<OneOf<Success, PortError>> RefreshRoomSnapshotsAsync(HouseholdTask task, CancellationToken ct)
    {
        var room = await rooms.FindAsync(task.RoomId, ct).ConfigureAwait(false);
        if (room.TryPickT2(out var roomError, out var roomRest))
        {
            return roomError;
        }

        var read = await settings.GetAsync(ct).ConfigureAwait(false);
        if (read.TryPickT2(out var settingsError, out var settingsRest))
        {
            return settingsError;
        }

        if (!roomRest.TryPickT0(out var found, out _) || !settingsRest.TryPickT0(out var current, out _))
        {
            return new Success();
        }

        var zone = DayKeys.FindZone(current.Timezone);
        var fromInstant = DayKeys.FromDayKey(DayKeys.Today(zone, time.GetUtcNow()), zone);
        var updated = await occurrences.UpdateUpcomingRoomSnapshotsAsync(task.Id, fromInstant, found.Id, found.Name, ct).ConfigureAwait(false);
        return updated.Match<OneOf<Success, PortError>>(_ => new Success(), error => error);
    }

    public async Task<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError, PreconditionFailed>> DeleteAsync(
        Actor actor, string id, CancellationToken cancellationToken, int? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!TaskRules.IsId(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var ran = await transactions.RunAsync(ct => DeleteInTransactionAsync(actor, id, expectedVersion, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError, PreconditionFailed>>(
            outcome => outcome.Match<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError, PreconditionFailed>>(
                done => done, notFound => notFound, conflict => conflict, error => error, stale => stale),
            conflict => conflict,
            error => error);
    }

    /// <summary>
    /// Port of the delete route, <c>removeTaskFromPlans</c>, <c>deleteTask</c> and <c>removeTaskFromBadgeRules</c>, in this order and in one transaction (the Node
    /// server ran them one after the other): the plans, the task, then the badge rules, whose use case joins this transaction. Occurrences stay as
    /// they are.
    /// </summary>
    private async Task<TransactionOutcome<OneOf<Success, NotFound, ConflictError, PortError, PreconditionFailed>>> DeleteInTransactionAsync(
        Actor actor, string id, int? expectedVersion, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<Success, NotFound, ConflictError, PortError, PreconditionFailed>> Abort(OneOf<Success, NotFound, ConflictError, PortError, PreconditionFailed> value) =>
            TransactionOutcome.Abort(value);

        var found = await tasks.FindAsync(id, ct).ConfigureAwait(false);
        if (found.TryPickT1(out var notFound, out var foundRest))
        {
            return Abort(notFound);
        }

        if (foundRest.TryPickT1(out var findError, out var task))
        {
            return Abort(findError);
        }

        if (EntityVersion.Check(expectedVersion, task.Version) is { } stale)
        {
            return Abort(stale);
        }

        var actorForAudit = AuditActor.From(actor);
        var holding = await plans.ListHoldingTaskAsync(task.Id, ct).ConfigureAwait(false);
        if (holding.TryPickT1(out var listError, out var held))
        {
            return Abort(listError);
        }

        foreach (var plan in held)
        {
            var remaining = CyclePlanSlots.Sort(plan.Slots.Where(slot => slot.TaskId != task.Id));
            var diff = CyclePlanSlots.Diff(plan.Slots, remaining);
            if (diff.IsEmpty)
            {
                continue;
            }

            var replaced = await plans.ReplaceSlotsAsync(plan.Id, remaining, time.GetUtcNow(), ct).ConfigureAwait(false);
            if (!replaced.TryPickT0(out _, out var replaceFailure))
            {
                return Abort(replaceFailure.Match<OneOf<Success, NotFound, ConflictError, PortError, PreconditionFailed>>(
                    notFound => notFound, error => error, _ => new PortError("tasks.delete: an unconditional plan write reported a version conflict.")));
            }

            var recordedPlan = await audit.RecordAsync(CyclePlanAudit.ForTaskDelete(actorForAudit, plan.Id, task.Id, diff), ct).ConfigureAwait(false);
            if (recordedPlan.TryPickT1(out var planAuditError, out _))
            {
                return Abort(planAuditError);
            }
        }

        var deleted = await tasks.DeleteAsync(task.Id, ct, expectedVersion).ConfigureAwait(false);
        if (!deleted.TryPickT0(out _, out var deleteFailure))
        {
            return Abort(deleteFailure.Match<OneOf<Success, NotFound, ConflictError, PortError, PreconditionFailed>>(
                notFound => notFound, error => error, failed => failed));
        }

        var recorded = await audit.RecordAsync(TaskAudit.ForDelete(actorForAudit, task), ct).ConfigureAwait(false);
        if (recorded.TryPickT1(out var auditError, out _))
        {
            return Abort(auditError);
        }

        var rules = await badges.RemoveTaskFromRulesAsync(actor, task.Id, ct).ConfigureAwait(false);
        return rules.Match(
            _ => TransactionOutcome.Commit<OneOf<Success, NotFound, ConflictError, PortError, PreconditionFailed>>(new Success()),
            rejected => Abort(new PortError("tasks.delete: the badge rules rejected the task id: " + string.Join(", ", rejected.Errors.Keys))),
            conflict => Abort(conflict),
            error => Abort(error));
    }

    public async Task<OneOf<int, NotFound, ValidationErrors, ConflictError, PortError>> BulkUpdateRoomAsync(Actor actor, string roomId, BulkRoomChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(change);
        if (!TaskRules.IsId(roomId))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        if (change is BulkRoomChange.Reassign { UserId: { } userId } && !TaskRules.IsId(userId))
        {
            return ValidationErrors.For("defaultAssigneeId", "invalid_object_id");
        }

        var ran = await transactions.RunAsync(ct => BulkInTransactionAsync(actor, roomId, change, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<int, NotFound, ValidationErrors, ConflictError, PortError>>(
            outcome => outcome.Match<OneOf<int, NotFound, ValidationErrors, ConflictError, PortError>>(
                count => count, notFound => notFound, errors => errors, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<int, NotFound, ValidationErrors, PortError>>> BulkInTransactionAsync(Actor actor, string roomId, BulkRoomChange change, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<int, NotFound, ValidationErrors, PortError>> Abort(OneOf<int, NotFound, ValidationErrors, PortError> value) =>
            TransactionOutcome.Abort(value);

        var room = await rooms.FindAsync(roomId, ct).ConfigureAwait(false);
        if (room.TryPickT1(out var notFound, out var roomRest))
        {
            return Abort(notFound);
        }

        if (roomRest.TryPickT1(out var roomError, out _))
        {
            return Abort(roomError);
        }

        if (change is BulkRoomChange.Reassign reassign)
        {
            var checkedReferences = await CheckReferencesAsync(null, null, reassign.UserId, ct).ConfigureAwait(false);
            if (checkedReferences.TryPickT1(out var errors, out var rest))
            {
                return Abort(errors);
            }

            if (rest.TryPickT1(out var referenceError, out _))
            {
                return Abort(referenceError);
            }
        }

        var listed = await tasks.ListActiveInRoomAsync(roomId, ct).ConfigureAwait(false);
        if (listed.TryPickT1(out var listError, out var active))
        {
            return Abort(listError);
        }

        var actorForAudit = AuditActor.From(actor);
        var changed = 0;
        foreach (var before in active)
        {
            var after = change switch
            {
                BulkRoomChange.Deactivate => before with { Active = false },
                BulkRoomChange.Reassign r => before with { DefaultAssigneeId = r.UserId },
                _ => before,
            };
            var changes = ChangeSet.Between(TaskAudit.Fields(before), TaskAudit.Fields(after));
            if (changes.IsNoOp)
            {
                continue;
            }

            var updated = await tasks.UpdateAsync(before.Id, ChangesBetween(before, after), time.GetUtcNow(), ct).ConfigureAwait(false);
            if (!updated.TryPickT0(out _, out var updateFailure))
            {
                return Abort(updateFailure.Match<OneOf<int, NotFound, ValidationErrors, PortError>>(
                    notFound => notFound, error => error, _ => new PortError("tasks.bulk: an unconditional write reported a version conflict.")));
            }

            var recorded = await RecordAsync(TaskAudit.ForChange(actorForAudit, before.Id, changes), ct).ConfigureAwait(false);
            if (recorded.TryPickT1(out var auditError, out _))
            {
                return Abort(auditError);
            }

            changed++;
        }

        return TransactionOutcome.Commit<OneOf<int, NotFound, ValidationErrors, PortError>>(changed);
    }

    /// <summary>
    /// Checks that the referenced room (active), interval and person (active) exist, and names every field that fails, like
    /// <c>assertTaskReferences</c>. A missing reference is not checked; a missing settings document means no interval exists.
    /// </summary>
    private async Task<OneOf<Success, ValidationErrors, PortError>> CheckReferencesAsync(string? roomId, string? intervalKey, string? assigneeId, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (roomId is not null)
        {
            var room = await rooms.FindAsync(roomId, ct).ConfigureAwait(false);
            if (room.TryPickT2(out var roomError, out var roomRest))
            {
                return roomError;
            }

            roomRest.Switch(
                found =>
                {
                    if (!found.Active)
                    {
                        errors["roomId"] = ["inactive_room"];
                    }
                },
                _ => errors["roomId"] = ["unknown_room"]);
        }

        if (intervalKey is not null)
        {
            var read = await settings.GetAsync(ct).ConfigureAwait(false);
            if (read.TryPickT2(out var settingsError, out var settingsRest))
            {
                return settingsError;
            }

            var known = settingsRest.Match(current => current.Intervals.Any(i => i.Key == intervalKey), _ => false);
            if (!known)
            {
                errors["intervalKey"] = ["unknown_interval"];
            }
        }

        if (assigneeId is not null)
        {
            var user = await users.FindAsync(assigneeId, ct).ConfigureAwait(false);
            if (user.TryPickT2(out var userError, out var userRest))
            {
                return userError;
            }

            userRest.Switch(
                found =>
                {
                    if (!found.Active)
                    {
                        errors["defaultAssigneeId"] = ["inactive_user"];
                    }
                },
                _ => errors["defaultAssigneeId"] = ["unknown_user"]);
        }

        return errors.Count == 0 ? new Success() : new ValidationErrors(errors);
    }

    private async Task<OneOf<Success, PortError>> RecordAsync(IReadOnlyList<AuditEntry> entries, CancellationToken ct)
    {
        foreach (var entry in entries)
        {
            var recorded = await audit.RecordAsync(entry, ct).ConfigureAwait(false);
            if (recorded.TryPickT1(out var error, out _))
            {
                return error;
            }
        }

        return new Success();
    }

    private static HouseholdTask Apply(HouseholdTask before, TaskPatch patch) => before with
    {
        Name = patch.Name ?? before.Name,
        RoomId = patch.RoomId ?? before.RoomId,
        IntervalKey = patch.IntervalKey ?? before.IntervalKey,
        DurationMinutes = patch.DurationMinutes ?? before.DurationMinutes,
        Points = patch.ResetPoints
            ? TaskPoints.DefaultForDuration(patch.DurationMinutes ?? before.DurationMinutes)
            : patch.Points ?? before.Points,
        DefaultAssigneeId = patch.DefaultAssignee is { } choice ? choice.UserId : before.DefaultAssigneeId,
        Active = patch.Active ?? before.Active,
        Notes = patch.Notes ?? before.Notes,
        Tags = patch.Tags ?? before.Tags,
    };

    /// <summary>Only the fields that differ, so the store sets exactly them.</summary>
    private static TaskChanges ChangesBetween(HouseholdTask before, HouseholdTask after) => new(
        after.Name != before.Name ? after.Name : null,
        after.RoomId != before.RoomId ? after.RoomId : null,
        after.IntervalKey != before.IntervalKey ? after.IntervalKey : null,
        after.DurationMinutes != before.DurationMinutes ? after.DurationMinutes : null,
        after.Points != before.Points ? after.Points : null,
        after.DefaultAssigneeId != before.DefaultAssigneeId ? new AssigneeChoice(after.DefaultAssigneeId) : null,
        after.Active != before.Active ? after.Active : null,
        after.Notes != before.Notes ? after.Notes : null,
        after.Tags.SequenceEqual(before.Tags, StringComparer.Ordinal) ? null : after.Tags);
}
