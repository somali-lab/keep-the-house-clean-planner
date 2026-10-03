using System.Globalization;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Tasks;
using OneOf;

namespace Huishoudplanner.Application.Occurrences;

/// <summary>
/// Extra executions, one-off tasks and the retract of recorded work (requirements 4.4, ADR-0009). Port of <c>createAdhocOccurrence</c>,
/// <c>createOneOffOccurrence</c> and <c>retractOccurrence</c> of <c>domain/occurrences.ts</c> and the ad-hoc parts of <c>data/occurrences.ts</c>.
/// Every write is one transaction: the occurrence, its audit entry and, for work that moves a completion, the task's <c>lastCompletedAt</c> with
/// its own entry. A replay and a refused request write and audit nothing.
/// </summary>
/// <remarks>
/// <para>Idempotency: the request key is looked up first, so a repeat replays the stored record whatever else changed since. Two requests with
/// one key at once both find nothing and both insert; the unique index lets exactly one commit. The loser fails inside its transaction
/// (a duplicate key, or a write conflict the transaction runner retries), the attempt is rolled back and started again, and its second
/// lookup finds the winner: replay for the same request, <c>idempotency_key_conflict</c> for another.</para>
/// <para>Not here yet: the points ledger entry that follows recorded work (<c>syncExecutionPoints</c>, phase 4); the <c>pointsSnapshot</c> of recorded
/// work is written, because it is a field of the occurrence.</para>
/// </remarks>
public sealed class AdhocOccurrenceService(
    ForStoringOccurrences occurrences,
    ForStoringTasks tasks,
    ForStoringUsers users,
    ForStoringRooms rooms,
    ForStoringSettings settings,
    ForStoringCycles cycles,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IAdhocOccurrenceService
{
    /// <summary>A lost race for a request key is settled by the winner's record; a handful of attempts is far more than one race needs.</summary>
    private const int MaxAttempts = 3;

    private readonly LastCompletedAtRefresh lastCompleted = new(occurrences, tasks, audit);

    private sealed record Context(HouseholdSettings Settings, TimeZoneInfo Zone, DateTimeOffset Now)
    {
        public OccurrenceView ViewOf(Occurrence occurrence) =>
            OccurrenceView.From(occurrence, Zone, Settings.CycleAnchorDate, DayKeys.Today(Zone, Now));
    }

    /// <summary>The outcome of one attempt: the result, or <see langword="null"/> when the insert lost the race for its request key.</summary>
    private readonly record struct Attempt(AdhocResult? Result);

    // ---- extra execution

    public async Task<OneOf<AdhocResult, ValidationErrors, ConflictError, SettingsMissing, PortError>> CreateExtraAsync(
        Actor actor, ExtraExecutionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        if (AdhocRules.Validate(command) is { } invalid)
        {
            return invalid;
        }

        var normalised = command with
        {
            TaskId = command.TaskId.ToLowerInvariant(),
            Assignee = command.Assignee is { UserId: { } person } ? new AssigneeChoice(person.ToLowerInvariant()) : command.Assignee,
        };
        return await CreateAsync(ct => ExtraCoreAsync(AuditActor.From(actor), ActorId(actor), normalised, ct), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An extra execution of an existing task: planned on a day outside the template or, with <c>done</c>, recorded as already done today. Only
    /// within cycles that are already generated, so exports and generation never see a half-filled cycle. Several executions of one task on one day coexist.
    /// </summary>
    private async Task<Step<Attempt>> ExtraCoreAsync(AuditActor actor, string actorId, ExtraExecutionCommand command, CancellationToken ct)
    {
        if (!(await ReadContextAsync(ct).ConfigureAwait(false)).TryGet(out var context, out var contextFailure))
        {
            return contextFailure;
        }

        var identity = new AdhocIdentity(command.TaskId, string.Empty, command.Date, command.Done);
        if (!(await ReplayAsync(command.RequestId, identity, context, ct).ConfigureAwait(false)).TryGet(out var replay, out var replayFailure))
        {
            return replayFailure;
        }

        if (replay is not null)
        {
            return new Attempt(replay);
        }

        if (!(await tasks.FindAsync(command.TaskId, ct).ConfigureAwait(false)).AsOptional().TryGet(out var task, out var taskFailure))
        {
            return taskFailure;
        }

        if (task is not { Active: true })
        {
            return ValidationErrors.For("taskId", task is null ? "unknown_task" : "inactive_task");
        }

        if (AdhocRules.CheckRecordedWork(command.Done, command.Date, DayKeys.Today(context.Zone, context.Now), command.Assignee) is { } recorded)
        {
            return recorded;
        }

        if (!(await rooms.FindAsync(task.RoomId, ct).ConfigureAwait(false)).AsOptional().TryGet(out var room, out var roomFailure))
        {
            return roomFailure;
        }

        var assigneeId = AdhocRules.ResolveAssignee(command.Assignee, command.Done, actorId, task.DefaultAssigneeId);
        if (!(await CheckAssigneeAsync(assigneeId, ct).ConfigureAwait(false)).TryGet(out _, out var assigneeFailure))
        {
            return assigneeFailure;
        }

        if (!(await FindCycleAsync(command.Date, context, ct).ConfigureAwait(false)).TryGet(out var cycle, out var cycleFailure))
        {
            return cycleFailure;
        }

        var date = DayKeys.FromDayKey(command.Date, context.Zone);
        // Also when recording it as done: the client can then offer to check off the planned occurrence instead.
        if (!(await occurrences.CountOpenOfTaskOnAsync(task.Id, date, ct).ConfigureAwait(false)).AsStep().TryGet(out var planned, out var plannedFailure))
        {
            return plannedFailure;
        }

        OccurrenceWarning[] warnings = planned > 0 ? [AdhocRules.AlreadyPlanned(task.Id, command.Date)] : [];
        var draft = new NewAdhocOccurrence(
            task.Id,
            cycle.Id,
            date,
            assigneeId,
            command.Done,
            command.Done ? context.Now : null,
            task.DurationMinutes,
            task.Name,
            task.RoomId,
            room?.Name,
            command.RequestId,
            command.Done ? task.Points : null,
            null,
            context.Now);
        return await InsertAsync(actor, draft, AdhocKind.Extra, warnings, context, ct).ConfigureAwait(false);
    }

    // ---- one-off task

    public async Task<OneOf<AdhocResult, ValidationErrors, ConflictError, SettingsMissing, PortError>> CreateOneOffAsync(
        Actor actor, OneOffCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        if (AdhocRules.Validate(command) is { } invalid)
        {
            return invalid;
        }

        var normalised = command with
        {
            Name = command.Name.Trim(),
            RoomId = command.RoomId?.ToLowerInvariant(),
            Assignee = command.Assignee is { UserId: { } person } ? new AssigneeChoice(person.ToLowerInvariant()) : command.Assignee,
        };
        return await CreateAsync(ct => OneOffCoreAsync(AuditActor.From(actor), ActorId(actor), normalised, ct), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A one-off task: an ad-hoc occurrence without a task record. Name, duration and room live in the snapshot fields only, so it never shows
    /// up in the task list, the due list, the planner or the AI input. A missing room is stored as null.
    /// </summary>
    private async Task<Step<Attempt>> OneOffCoreAsync(AuditActor actor, string actorId, OneOffCommand command, CancellationToken ct)
    {
        if (!(await ReadContextAsync(ct).ConfigureAwait(false)).TryGet(out var context, out var contextFailure))
        {
            return contextFailure;
        }

        var identity = new AdhocIdentity(null, command.Name, command.Date, command.Done);
        if (!(await ReplayAsync(command.RequestId, identity, context, ct).ConfigureAwait(false)).TryGet(out var replay, out var replayFailure))
        {
            return replayFailure;
        }

        if (replay is not null)
        {
            return new Attempt(replay);
        }

        Domain.Rooms.Room? room = null;
        if (command.RoomId is not null)
        {
            if (!(await rooms.FindAsync(command.RoomId, ct).ConfigureAwait(false)).AsOptional().TryGet(out room, out var roomFailure))
            {
                return roomFailure;
            }

            if (room is not { Active: true })
            {
                return ValidationErrors.For("roomId", room is null ? "unknown_room" : "inactive_room");
            }
        }

        if (AdhocRules.CheckRecordedWork(command.Done, command.Date, DayKeys.Today(context.Zone, context.Now), command.Assignee) is { } recorded)
        {
            return recorded;
        }

        var assigneeId = AdhocRules.ResolveAssignee(command.Assignee, command.Done, actorId, null);
        if (!(await CheckAssigneeAsync(assigneeId, ct).ConfigureAwait(false)).TryGet(out _, out var assigneeFailure))
        {
            return assigneeFailure;
        }

        if (!(await FindCycleAsync(command.Date, context, ct).ConfigureAwait(false)).TryGet(out var cycle, out var cycleFailure))
        {
            return cycleFailure;
        }

        // A one-off task has no task value: the chosen points, else the duration rule (ADR-0011). The chosen value stays on the occurrence,
        // so a one-off task planned now and completed later keeps it.
        var draft = new NewAdhocOccurrence(
            null,
            cycle.Id,
            DayKeys.FromDayKey(command.Date, context.Zone),
            assigneeId,
            command.Done,
            command.Done ? context.Now : null,
            command.DurationMinutes,
            command.Name,
            room?.Id,
            room?.Name,
            command.RequestId,
            command.Done ? command.Points ?? TaskPoints.DefaultForDuration(command.DurationMinutes) : null,
            command.Points,
            context.Now);
        return await InsertAsync(actor, draft, AdhocKind.OneOff, [], context, ct).ConfigureAwait(false);
    }

    // ---- retract

    public async Task<OneOf<Success, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> RetractAsync(
        Actor actor, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!OccurrenceRules.IsId(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var ran = await transactions.RunAsync(
            async ct =>
            {
                var step = await RetractCoreAsync(AuditActor.From(actor), id.ToLowerInvariant(), ct).ConfigureAwait(false);
                return step.IsSuccess ? TransactionOutcome.Commit(step) : TransactionOutcome.Abort(step);
            },
            cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<Success, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>>(
            step => step.ToOneOf(_ => new Success()).Match<OneOf<Success, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>>(
                success => success, notFound => notFound, invalid => invalid, conflict => conflict, missing => missing, error => error),
            conflict => conflict,
            error => error);
    }

    /// <summary>
    /// Undo of recorded work: an audited delete (reason retract) that also restores <c>lastCompletedAt</c>. It is an undo, not a correction: only
    /// work of today can be retracted by any profile; deleting older completions stays an administrator's correction.
    /// </summary>
    private async Task<Step<bool>> RetractCoreAsync(AuditActor actor, string id, CancellationToken ct)
    {
        if (!(await occurrences.FindAsync(id, ct).ConfigureAwait(false)).AsStep().TryGet(out var current, out var findFailure))
        {
            return findFailure;
        }

        if (!AdhocRules.IsRetractable(current))
        {
            return new ConflictError("not_retractable", "Only recorded extra work can be retracted");
        }

        if (!(await ReadContextAsync(ct).ConfigureAwait(false)).TryGet(out var context, out var contextFailure))
        {
            return contextFailure;
        }

        if (DayKeys.ToDayKey(current.Date, context.Zone) != DayKeys.Today(context.Zone, context.Now))
        {
            return new ConflictError("retract_not_today", "Only work recorded today can be retracted");
        }

        // A concurrent retract already removed it: the same answer as a second retract.
        if (!(await occurrences.DeleteRecordedAsync(id, ct).ConfigureAwait(false)).AsStep().TryGet(out var deleted, out var deleteFailure))
        {
            return deleteFailure;
        }

        if (!(await RecordAsync(OccurrenceAudit.ForRetracted(actor, deleted), ct).ConfigureAwait(false)).TryGet(out _, out var auditFailure))
        {
            return auditFailure;
        }

        if (!(await lastCompleted.RunAsync(actor, deleted.TaskId, id, context.Now, ct).ConfigureAwait(false)).TryGet(out _, out var refreshFailure))
        {
            return refreshFailure;
        }

        // The ledger entry is removed with it in phase 4 (syncExecutionPoints, reason retract).
        return true;
    }

    // ---- building blocks

    private static string ActorId(Actor actor) => actor.ActorId.ToLowerInvariant();

    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());

    private async Task<Step<Context>> ReadContextAsync(CancellationToken ct)
    {
        var read = await settings.GetAsync(ct).ConfigureAwait(false);
        return read.Match<Step<Context>>(
            current => new Context(current, DayKeys.FindZone(current.Timezone), Now()),
            missing => missing,
            error => error);
    }

    /// <summary>
    /// The stored record of a repeated request key: the same request replays it (nothing is written, no warnings), another request is
    /// <c>idempotency_key_conflict</c>. <see langword="null"/> when there is no key or nothing holds it.
    /// </summary>
    private async Task<Step<AdhocResult?>> ReplayAsync(string? requestId, AdhocIdentity identity, Context context, CancellationToken ct)
    {
        if (requestId is null)
        {
            return (AdhocResult?)null;
        }

        if (!(await occurrences.FindByRequestIdAsync(requestId, ct).ConfigureAwait(false)).AsOptional().TryGet(out var existing, out var failure))
        {
            return failure;
        }

        if (existing is null)
        {
            return (AdhocResult?)null;
        }

        return AdhocRules.IsReplay(existing, identity, context.Zone)
            ? new AdhocResult(context.ViewOf(existing), [], false)
            : KeyConflict();
    }

    private static ConflictError KeyConflict() => new("idempotency_key_conflict", "This request key was already used for a different request");

    private async Task<Step<bool>> CheckAssigneeAsync(string? assigneeId, CancellationToken ct)
    {
        if (assigneeId is null)
        {
            return true;
        }

        if (!(await users.FindAsync(assigneeId, ct).ConfigureAwait(false)).AsOptional().TryGet(out var person, out var failure))
        {
            return failure;
        }

        if (person is not { Active: true })
        {
            return ValidationErrors.For("assigneeId", person is null ? "unknown_user" : "inactive_user");
        }

        return true;
    }

    private async Task<Step<Cycle>> FindCycleAsync(DateOnly day, Context context, CancellationToken ct)
    {
        var found = await cycles.FindByIndexAsync(Cycles.CycleIndexFor(day, context.Settings.CycleAnchorDate), ct).ConfigureAwait(false);
        return found.Match<Step<Cycle>>(
            cycle => cycle,
            _ => new ConflictError(
                "cycle_not_generated",
                "That day is not generated yet",
                new Dictionary<string, object?> { ["date"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }),
            error => error);
    }

    /// <summary>Inserts the record and audits it; losing the race for the request key ends the attempt without a result.</summary>
    private async Task<Step<Attempt>> InsertAsync(
        AuditActor actor, NewAdhocOccurrence draft, AdhocKind kind, IReadOnlyList<OccurrenceWarning> warnings, Context context, CancellationToken ct)
    {
        var inserted = await occurrences.InsertAdhocAsync(draft, ct).ConfigureAwait(false);
        if (inserted.IsT1)
        {
            return new Attempt(null);
        }

        if (!inserted.Match<Step<Occurrence>>(stored => stored, _ => throw new InvalidOperationException("Handled above."), error => error).TryGet(out var occurrence, out var failure))
        {
            return failure;
        }

        if (!(await RecordAsync(OccurrenceAudit.ForAdhocCreated(actor, occurrence, kind), ct).ConfigureAwait(false)).TryGet(out _, out var auditFailure))
        {
            return auditFailure;
        }

        if (occurrence.Status == OccurrenceStatus.Done &&
            !(await lastCompleted.RunAsync(actor, occurrence.TaskId, occurrence.Id, context.Now, ct).ConfigureAwait(false)).TryGet(out _, out var refreshFailure))
        {
            return refreshFailure;
        }

        // The ledger entry of recorded work follows in phase 4 (syncExecutionPoints, reason recorded).
        return new Attempt(new AdhocResult(context.ViewOf(occurrence), warnings, true));
    }

    private async Task<Step<bool>> RecordAsync(AuditEntry entry, CancellationToken ct)
    {
        var recorded = await audit.RecordAsync(entry, ct).ConfigureAwait(false);
        return recorded.Match<Step<bool>>(_ => true, error => error);
    }

    /// <summary>
    /// Runs the creation as a transaction; an attempt that lost the race for its request key is rolled back and run again, which then
    /// finds the winner's record.
    /// </summary>
    private async Task<OneOf<AdhocResult, ValidationErrors, ConflictError, SettingsMissing, PortError>> CreateAsync(
        Func<CancellationToken, Task<Step<Attempt>>> attempt, CancellationToken cancellationToken)
    {
        for (var number = 1; ; number++)
        {
            var ran = await transactions.RunAsync(
                async ct =>
                {
                    var step = await attempt(ct).ConfigureAwait(false);
                    return step.TryGet(out var done, out _) && done.Result is not null ? TransactionOutcome.Commit(step) : TransactionOutcome.Abort(step);
                },
                cancellationToken).ConfigureAwait(false);
            if (!ran.TryPickT0(out var outcome, out var rest))
            {
                return rest.Match<OneOf<AdhocResult, ValidationErrors, ConflictError, SettingsMissing, PortError>>(conflict => conflict, error => error);
            }

            if (!outcome.TryGet(out var result, out var refusal))
            {
                return refusal.Value.Match<OneOf<AdhocResult, ValidationErrors, ConflictError, SettingsMissing, PortError>>(
                    _ => throw new InvalidOperationException("Creating an ad-hoc occurrence cannot miss a resource."),
                    invalid => invalid,
                    conflict => conflict,
                    missing => missing,
                    error => error);
            }

            if (result.Result is { } created)
            {
                return created;
            }

            if (number >= MaxAttempts)
            {
                // The key is held by a record nobody can see (removed in between again and again): the same answer as the Node server.
                return KeyConflict();
            }
        }
    }
}
