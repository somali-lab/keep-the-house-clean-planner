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
/// The occurrence use cases (requirements 4.4 and 4.8). Port of <c>routes/occurrences.ts</c>, <c>domain/occurrences.ts</c> and the update
/// helpers of <c>data/occurrences.ts</c>. Every write is one transaction with its audit entries, the occurrence entry first and then the
/// task entry for <c>lastCompletedAt</c>; a change that changes nothing writes and audits nothing (ADR-0004). The store write is guarded on the
/// state that was read, so a lost race becomes <c>invalid_transition</c> (or <c>already_claimed</c>) instead of a silent overwrite.
/// </summary>
/// <remarks>
/// Not here yet: the points ledger that follows a completion (<c>syncExecutionPoints</c>, phase 4) and the extra and one-off executions with their
/// request keys and the retract (slice 3.3). The <c>pointsSnapshot</c> of a completion is written, because it is a field of the occurrence.
/// </remarks>
public sealed class OccurrenceService(
    ForStoringOccurrences occurrences,
    ForStoringTasks tasks,
    ForStoringUsers users,
    ForStoringSettings settings,
    ForStoringCycles cycles,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IOccurrenceService
{
    /// <summary>What one use case reads once: the settings, the zone they name and the moment (whole milliseconds, as stored).</summary>
    private sealed record Context(HouseholdSettings Settings, TimeZoneInfo Zone, DateTimeOffset Now)
    {
        public OccurrenceView ViewOf(Occurrence occurrence) =>
            OccurrenceView.From(occurrence, Zone, Settings.CycleAnchorDate, DayKeys.Today(Zone, Now));
    }

    private sealed record Applied(Occurrence Stored, bool Changed);

    // ---- reads

    public async Task<OneOf<OccurrenceList, ValidationErrors, SettingsMissing, PortError>> ListAsync(OccurrenceListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.AssigneeId is not null && !OccurrenceRules.IsId(request.AssigneeId))
        {
            errors["assigneeId"] = ["invalid_object_id"];
        }

        if (request.From > request.To)
        {
            errors["from"] = ["from_after_to"];
        }

        var take = request.Limit ?? OccurrenceListQuery.DefaultLimit;
        if (take is < 1 or > OccurrenceListQuery.MaxLimit)
        {
            errors["limit"] = ["must be a whole number from 1 to " + OccurrenceListQuery.MaxLimit.ToString(CultureInfo.InvariantCulture)];
        }

        OccurrenceCursor? after = null;
        if (request.Cursor is not null)
        {
            if (OccurrenceCursor.TryDecode(request.Cursor, out var decoded))
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

        var read = await ReadContextAsync(cancellationToken).ConfigureAwait(false);
        if (!read.TryGet(out var context, out var failure))
        {
            return Narrow<OccurrenceList>(failure);
        }

        var query = new OccurrenceQuery(
            DayKeys.FromDayKey(request.From, context.Zone),
            DayKeys.FromDayKey(DayKeys.AddDays(request.To, 1), context.Zone),
            request.AssigneeId?.ToLowerInvariant(),
            request.Status,
            after,
            take + 1);
        var found = await occurrences.ListAsync(query, cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<OccurrenceList, ValidationErrors, SettingsMissing, PortError>>(
            items => items.Count > take
                ? new OccurrenceList([.. items.Take(take).Select(context.ViewOf)], OccurrenceCursor.After(items[take - 1]).Encode())
                : new OccurrenceList([.. items.Select(context.ViewOf)], null),
            error => error);
    }

    public async Task<OneOf<OccurrenceView, NotFound, ValidationErrors, SettingsMissing, PortError>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!OccurrenceRules.IsId(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var read = await ReadContextAsync(cancellationToken).ConfigureAwait(false);
        if (!read.TryGet(out var context, out var failure))
        {
            return NarrowGet<OccurrenceView>(failure);
        }

        var found = await occurrences.FindAsync(id.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<OccurrenceView, NotFound, ValidationErrors, SettingsMissing, PortError>>(
            occurrence => context.ViewOf(occurrence),
            notFound => notFound,
            error => error);
    }

    private static OneOf<T, ValidationErrors, SettingsMissing, PortError> Narrow<T>(Refusal failure) =>
        failure.Value.Match<OneOf<T, ValidationErrors, SettingsMissing, PortError>>(
            _ => throw new InvalidOperationException("A read of the settings cannot miss an occurrence."),
            invalid => invalid,
            _ => throw new InvalidOperationException("A read cannot conflict."),
            missing => missing,
            error => error);

    private static OneOf<T, NotFound, ValidationErrors, SettingsMissing, PortError> NarrowGet<T>(Refusal failure) =>
        failure.Value.Match<OneOf<T, NotFound, ValidationErrors, SettingsMissing, PortError>>(
            notFound => notFound,
            invalid => invalid,
            _ => throw new InvalidOperationException("A read cannot conflict."),
            missing => missing,
            error => error);

    // ---- complete, uncomplete, correct, delete

    public async Task<OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> CompleteAsync(
        Actor actor, string id, CompleteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        if (Precondition(id) is { } bad)
        {
            return bad;
        }

        if (OccurrenceRules.Validate(command) is { } invalid)
        {
            return invalid;
        }

        return await RunViewAsync(ct => CompleteCoreAsync(AuditActor.From(actor), ActorId(actor), id.ToLowerInvariant(), command, ct), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Step<(Context, Occurrence)>> CompleteCoreAsync(AuditActor actor, string actorId, string id, CompleteCommand command, CancellationToken ct)
    {
        if (!(await ReadContextAsync(ct).ConfigureAwait(false)).TryGet(out var context, out var contextFailure))
        {
            return contextFailure;
        }

        if (!(await RequireAsync(id, ct).ConfigureAwait(false)).TryGet(out var current, out var findFailure))
        {
            return findFailure;
        }

        if (current.Status == OccurrenceStatus.Done)
        {
            return InvalidTransition(current.Status, "complete");
        }

        // completedBy is the person credited (ADR-0011). Work of someone else never defaults to the assignee: the request must say who
        // performed it, either a named person or a take over.
        if (OccurrenceRules.NeedsCompletionChoice(current, actorId, command))
        {
            return ValidationErrors.For("completedBy", "completion_choice_required");
        }

        var completedBy = command.TakeOver ? actorId : command.CompletedBy?.ToLowerInvariant() ?? actorId;
        if (command.CompletedBy is not null)
        {
            if (!(await users.FindAsync(completedBy, ct).ConfigureAwait(false)).AsOptional().TryGet(out var person, out var userFailure))
            {
                return userFailure;
            }

            if (person is not { Active: true })
            {
                return ValidationErrors.For("completedBy", person is null ? "unknown_user" : "inactive_user");
            }
        }

        if (!(await PointsSnapshotAsync(current, ct).ConfigureAwait(false)).TryGet(out var points, out var pointsFailure))
        {
            return pointsFailure;
        }

        var transition = OccurrenceTransitions.Complete(current, actorId, completedBy, command.TakeOver, points, context.Now, context.Zone);
        if (!(await ApplyAsync(actor, current, transition, new OccurrenceGuard(current.Status), "complete", context, ct).ConfigureAwait(false)).TryGet(out var applied, out var applyFailure))
        {
            return applyFailure;
        }

        if (!(await RefreshLastCompletedAtAsync(actor, current.TaskId, id, context, ct).ConfigureAwait(false)).TryGet(out _, out var refreshFailure))
        {
            return refreshFailure;
        }

        // The points ledger entry of this execution follows in phase 4 (syncExecutionPoints).
        return (context, applied.Stored);
    }

    public async Task<OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> UncompleteAsync(
        Actor actor, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Precondition(id) is { } bad)
        {
            return bad;
        }

        return await RunViewAsync(ct => UncompleteCoreAsync(AuditActor.From(actor), id.ToLowerInvariant(), ct), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Step<(Context, Occurrence)>> UncompleteCoreAsync(AuditActor actor, string id, CancellationToken ct)
    {
        if (!(await RequireAsync(id, ct).ConfigureAwait(false)).TryGet(out var current, out var findFailure))
        {
            return findFailure;
        }

        if (current.Status != OccurrenceStatus.Done)
        {
            return InvalidTransition(current.Status, "uncomplete");
        }

        // Recorded work has no planned state to return to; reopening it would leave a record that later counts as missed.
        if (current.RecordedDone)
        {
            return new ConflictError("retract_required", "Recorded work cannot be uncompleted; retract it instead");
        }

        if (!(await ReadContextAsync(ct).ConfigureAwait(false)).TryGet(out var context, out var contextFailure))
        {
            return contextFailure;
        }

        var transition = OccurrenceTransitions.Uncomplete(current, context.Now);
        if (!(await ApplyAsync(actor, current, transition, new OccurrenceGuard(OccurrenceStatus.Done), "uncomplete", context, ct).ConfigureAwait(false)).TryGet(out var applied, out var applyFailure))
        {
            return applyFailure;
        }

        if (!(await RefreshLastCompletedAtAsync(actor, current.TaskId, id, context, ct).ConfigureAwait(false)).TryGet(out _, out var refreshFailure))
        {
            return refreshFailure;
        }

        return (context, applied.Stored);
    }

    public async Task<OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> EditCompletionAsync(
        Actor actor, string id, EditCompletionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        if (Precondition(id) is { } bad)
        {
            return bad;
        }

        if (!OccurrenceRules.IsId(command.CompletedBy))
        {
            return ValidationErrors.For("completedBy", "invalid_object_id");
        }

        return await RunViewAsync(ct => EditCompletionCoreAsync(AuditActor.From(actor), id.ToLowerInvariant(), command with { CompletedBy = command.CompletedBy.ToLowerInvariant() }, ct), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Step<(Context, Occurrence)>> EditCompletionCoreAsync(AuditActor actor, string id, EditCompletionCommand command, CancellationToken ct)
    {
        if (!(await ReadContextAsync(ct).ConfigureAwait(false)).TryGet(out var context, out var contextFailure))
        {
            return contextFailure;
        }

        if (!(await RequireAsync(id, ct).ConfigureAwait(false)).TryGet(out var current, out var findFailure))
        {
            return findFailure;
        }

        if (current.Status != OccurrenceStatus.Done)
        {
            return InvalidTransition(current.Status, "edit completion of");
        }

        if (!(await users.FindAsync(command.CompletedBy, ct).ConfigureAwait(false)).AsOptional().TryGet(out var person, out var userFailure))
        {
            return userFailure;
        }

        if (person is null)
        {
            return ValidationErrors.For("completedBy", "unknown_user");
        }

        string? newCycleId = null;
        if (command.Date != DayKeys.ToDayKey(current.Date, context.Zone))
        {
            if (!(await FindCycleAsync(command.Date, context, ct).ConfigureAwait(false)).TryGet(out var cycle, out var cycleFailure))
            {
                return cycleFailure;
            }

            newCycleId = cycle.Id != current.CycleId ? cycle.Id : null;
        }

        var transition = OccurrenceTransitions.EditCompletion(current, command, newCycleId, context.Now, context.Zone);
        if (!(await ApplyAsync(actor, current, transition, new OccurrenceGuard(OccurrenceStatus.Done), "edit completion of", context, ct).ConfigureAwait(false)).TryGet(out var applied, out var applyFailure))
        {
            return applyFailure;
        }

        if (!(await RefreshLastCompletedAtAsync(actor, current.TaskId, id, context, ct).ConfigureAwait(false)).TryGet(out _, out var refreshFailure))
        {
            return refreshFailure;
        }

        // The ledger entry follows the correction in phase 4 (syncExecutionPoints, reason correction).
        return (context, applied.Stored);
    }

    public async Task<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>> DeleteCompletedAsync(
        Actor actor, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!OccurrenceRules.IsId(id))
        {
            return ValidationErrors.For("id", "invalid_object_id");
        }

        var ran = await transactions.RunAsync(ct => InTransactionAsync(DeleteCoreAsync(AuditActor.From(actor), id.ToLowerInvariant(), ct)), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>>(
            step => step.ToOneOf(_ => new Success()).Match<OneOf<Success, NotFound, ValidationErrors, ConflictError, PortError>>(
                success => success,
                notFound => notFound,
                invalid => invalid,
                conflict => conflict,
                _ => throw new InvalidOperationException("A deletion does not read the settings."),
                error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<Step<bool>> DeleteCoreAsync(AuditActor actor, string id, CancellationToken ct)
    {
        if (!(await RequireAsync(id, ct).ConfigureAwait(false)).TryGet(out var current, out var findFailure))
        {
            return findFailure;
        }

        if (current.Status != OccurrenceStatus.Done)
        {
            return InvalidTransition(current.Status, "delete");
        }

        if (!(await occurrences.DeleteAsync([id], ct).ConfigureAwait(false)).AsStep().TryGet(out _, out var deleteFailure))
        {
            return deleteFailure;
        }

        if (!(await RecordAsync(OccurrenceAudit.ForCorrectionDelete(actor, current), ct).ConfigureAwait(false)).TryGet(out _, out var auditFailure))
        {
            return auditFailure;
        }

        var now = Now();
        if (!(await RefreshLastCompletedAtAsync(actor, current.TaskId, id, now, ct).ConfigureAwait(false)).TryGet(out _, out var refreshFailure))
        {
            return refreshFailure;
        }

        // The ledger entry is removed with it in phase 4 (syncExecutionPoints, reason correction).
        return true;
    }

    // ---- skip, reschedule, assign, claim

    public async Task<OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> SkipAsync(
        Actor actor, string id, string? reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Precondition(id) is { } bad)
        {
            return bad;
        }

        var (normalised, error) = OccurrenceRules.NormaliseSkipReason(reason);
        if (error is not null)
        {
            return error;
        }

        return await RunViewAsync(ct => SkipCoreAsync(AuditActor.From(actor), id.ToLowerInvariant(), normalised, ct), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Step<(Context, Occurrence)>> SkipCoreAsync(AuditActor actor, string id, string? reason, CancellationToken ct)
    {
        if (!(await RequireAsync(id, ct).ConfigureAwait(false)).TryGet(out var current, out var findFailure))
        {
            return findFailure;
        }

        if (current.Status != OccurrenceStatus.Open)
        {
            return InvalidTransition(current.Status, "skip");
        }

        if (!(await ReadContextAsync(ct).ConfigureAwait(false)).TryGet(out var context, out var contextFailure))
        {
            return contextFailure;
        }

        var transition = OccurrenceTransitions.Skip(current, reason, context.Now);
        if (!(await ApplyAsync(actor, current, transition, new OccurrenceGuard(OccurrenceStatus.Open), "skip", context, ct).ConfigureAwait(false)).TryGet(out var applied, out var applyFailure))
        {
            return applyFailure;
        }

        return (context, applied.Stored);
    }

    public async Task<OneOf<OccurrenceChange, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> RescheduleAsync(
        Actor actor, string id, DateOnly day, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Precondition(id) is { } bad)
        {
            return bad;
        }

        return await RunAsync(ct => RescheduleCoreAsync(AuditActor.From(actor), id.ToLowerInvariant(), day, ct), change => change, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Step<OccurrenceChange>> RescheduleCoreAsync(AuditActor actor, string id, DateOnly date, CancellationToken ct)
    {
        if (!(await ReadContextAsync(ct).ConfigureAwait(false)).TryGet(out var context, out var contextFailure))
        {
            return contextFailure;
        }

        if (!(await RequireAsync(id, ct).ConfigureAwait(false)).TryGet(out var current, out var findFailure))
        {
            return findFailure;
        }

        if (current.Status != OccurrenceStatus.Open)
        {
            return InvalidTransition(current.Status, "reschedule");
        }

        if (DayKeys.ToDayKey(current.Date, context.Zone) == date)
        {
            return new OccurrenceChange(context.ViewOf(current), []);
        }

        if (!(await FindCycleAsync(date, context, ct).ConfigureAwait(false)).TryGet(out var cycle, out var cycleFailure))
        {
            return cycleFailure;
        }

        var transition = OccurrenceTransitions.Reschedule(current, date, cycle.Id, context.Now, context.Zone);
        if (!(await ApplyAsync(actor, current, transition, new OccurrenceGuard(OccurrenceStatus.Open), "reschedule", context, ct).ConfigureAwait(false)).TryGet(out var applied, out var applyFailure))
        {
            return applyFailure;
        }

        if (!(await WarningsAsync(applied.Stored.AssigneeId, date, ct).ConfigureAwait(false)).TryGet(out var warnings, out var warningFailure))
        {
            return warningFailure;
        }

        return new OccurrenceChange(context.ViewOf(applied.Stored), warnings);
    }

    public async Task<OneOf<OccurrenceChange, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> AssignAsync(
        Actor actor, string id, string? assigneeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Precondition(id) is { } bad)
        {
            return bad;
        }

        if (assigneeId is not null && !OccurrenceRules.IsId(assigneeId))
        {
            return ValidationErrors.For("assigneeId", "invalid_object_id");
        }

        return await RunAsync(ct => AssignCoreAsync(AuditActor.From(actor), id.ToLowerInvariant(), assigneeId?.ToLowerInvariant(), ct), change => change, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Step<OccurrenceChange>> AssignCoreAsync(AuditActor actor, string id, string? assigneeId, CancellationToken ct)
    {
        if (!(await ReadContextAsync(ct).ConfigureAwait(false)).TryGet(out var context, out var contextFailure))
        {
            return contextFailure;
        }

        if (!(await RequireAsync(id, ct).ConfigureAwait(false)).TryGet(out var current, out var findFailure))
        {
            return findFailure;
        }

        if (current.Status != OccurrenceStatus.Open)
        {
            return InvalidTransition(current.Status, "assign");
        }

        if (assigneeId is not null)
        {
            if (!(await users.FindAsync(assigneeId, ct).ConfigureAwait(false)).AsOptional().TryGet(out var person, out var userFailure))
            {
                return userFailure;
            }

            if (person is not { Active: true })
            {
                return ValidationErrors.For("assigneeId", person is null ? "unknown_user" : "inactive_user");
            }
        }

        var transition = OccurrenceTransitions.Assign(current, assigneeId, context.Now, context.Zone);
        if (!(await ApplyAsync(actor, current, transition, new OccurrenceGuard(OccurrenceStatus.Open), "assign", context, ct).ConfigureAwait(false)).TryGet(out var applied, out var applyFailure))
        {
            return applyFailure;
        }

        OccurrenceWarning[] warnings = [];
        if (applied.Changed)
        {
            var day = DayKeys.ToDayKey(applied.Stored.Date, context.Zone);
            if (!(await WarningsAsync(assigneeId, day, ct).ConfigureAwait(false)).TryGet(out warnings!, out var warningFailure))
            {
                return warningFailure;
            }
        }

        return new OccurrenceChange(context.ViewOf(applied.Stored), warnings);
    }

    public async Task<OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> ClaimAsync(
        Actor actor, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Precondition(id) is { } bad)
        {
            return bad;
        }

        return await RunViewAsync(ct => ClaimCoreAsync(AuditActor.From(actor), ActorId(actor), id.ToLowerInvariant(), ct), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the actor as assignee only while the occurrence is open and unassigned. A completed or skipped occurrence is
    /// <c>409 invalid_transition</c>, an occurrence that has an assignee <c>409 already_claimed</c>; of two claims at once exactly one wins.
    /// </summary>
    private async Task<Step<(Context, Occurrence)>> ClaimCoreAsync(AuditActor actor, string actorId, string id, CancellationToken ct)
    {
        if (!(await ReadContextAsync(ct).ConfigureAwait(false)).TryGet(out var context, out var contextFailure))
        {
            return contextFailure;
        }

        if (!(await RequireAsync(id, ct).ConfigureAwait(false)).TryGet(out var current, out var findFailure))
        {
            return findFailure;
        }

        if (current.Status != OccurrenceStatus.Open)
        {
            return InvalidTransition(current.Status, "claim");
        }

        if (current.AssigneeId is not null)
        {
            return AlreadyClaimed();
        }

        var transition = OccurrenceTransitions.Claim(current, actorId, context.Now, context.Zone);
        var entry = OccurrenceAudit.ForChange(actor, current, transition.After, transition.Action, transition.Meta);
        var updated = await occurrences.UpdateAsync(current, transition.After, new OccurrenceGuard(OccurrenceStatus.Open, RequireUnassigned: true), context.Now, ct).ConfigureAwait(false);
        if (updated.TryPickT2(out _, out var rest))
        {
            // A concurrent write got there first: say what it made of the occurrence.
            if (!(await RequireAsync(id, ct).ConfigureAwait(false)).TryGet(out var latest, out var latestFailure))
            {
                return latestFailure;
            }

            return latest.Status != OccurrenceStatus.Open ? InvalidTransition(latest.Status, "claim") : AlreadyClaimed();
        }

        if (!rest.TryPickT0(out var stored, out var failure))
        {
            return failure.Match<Step<(Context, Occurrence)>>(notFound => notFound, error => error);
        }

        if (!(await RecordAsync(entry!, ct).ConfigureAwait(false)).TryGet(out _, out var auditFailure))
        {
            return auditFailure;
        }

        return (context, stored);
    }

    // ---- building blocks

    private static ValidationErrors? Precondition(string id) => OccurrenceRules.IsId(id) ? null : ValidationErrors.For("id", "invalid_object_id");

    private static string ActorId(Actor actor) => actor.ActorId.ToLowerInvariant();

    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());

    private static ConflictError InvalidTransition(OccurrenceStatus from, string action) => InvalidTransition(OccurrenceNames.ToWire(from), action);

    private static ConflictError InvalidTransition(string from, string action) => new(
        "invalid_transition",
        string.Create(CultureInfo.InvariantCulture, $"Cannot {action} an occurrence that is {from}"),
        new Dictionary<string, object?> { ["status"] = from, ["action"] = action });

    private static ConflictError AlreadyClaimed() => new("already_claimed", "Occurrence already has an assignee");

    private static ConflictError CycleNotGenerated(DateOnly date) => new(
        "cycle_not_generated",
        "That day is not generated yet",
        new Dictionary<string, object?> { ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });

    private async Task<Step<Context>> ReadContextAsync(CancellationToken ct)
    {
        var read = await settings.GetAsync(ct).ConfigureAwait(false);
        return read.Match<Step<Context>>(
            current => new Context(current, DayKeys.FindZone(current.Timezone), Now()),
            missing => missing,
            error => error);
    }

    private async Task<Step<Occurrence>> RequireAsync(string id, CancellationToken ct) =>
        (await occurrences.FindAsync(id, ct).ConfigureAwait(false)).AsStep();

    private async Task<Step<Cycle>> FindCycleAsync(DateOnly day, Context context, CancellationToken ct)
    {
        var found = await cycles.FindByIndexAsync(Cycles.CycleIndexFor(day, context.Settings.CycleAnchorDate), ct).ConfigureAwait(false);
        return found.Match<Step<Cycle>>(cycle => cycle, _ => CycleNotGenerated(day), error => error);
    }

    /// <summary>Writes the transition and its audit entry; a transition that changes nothing writes and audits nothing.</summary>
    private async Task<Step<Applied>> ApplyAsync(
        AuditActor actor, Occurrence current, OccurrenceTransition transition, OccurrenceGuard guard, string action, Context context, CancellationToken ct)
    {
        var entry = OccurrenceAudit.ForChange(actor, current, transition.After, transition.Action, transition.Meta);
        if (entry is null)
        {
            return new Applied(current, false);
        }

        var updated = await occurrences.UpdateAsync(current, transition.After, guard, context.Now, ct).ConfigureAwait(false);
        var stored = updated.Match<Step<Occurrence>>(
            occurrence => occurrence,
            notFound => notFound,
            _ => InvalidTransition("changed", action),
            error => error);
        if (!stored.TryGet(out var after, out var failure))
        {
            return failure;
        }

        if (!(await RecordAsync(entry, ct).ConfigureAwait(false)).TryGet(out _, out var auditFailure))
        {
            return auditFailure;
        }

        return new Applied(after, true);
    }

    /// <summary>
    /// <c>lastCompletedAt</c> is the newest <c>completedAt</c> of the task's done occurrences. A one-off task has no task record to refresh (ADR-0009),
    /// a task that is gone is skipped. Written, and audited with the occurrence that caused it, only when the value changes.
    /// </summary>
    private Task<Step<bool>> RefreshLastCompletedAtAsync(AuditActor actor, string? taskId, string occurrenceId, Context context, CancellationToken ct) =>
        RefreshLastCompletedAtAsync(actor, taskId, occurrenceId, context.Now, ct);

    private async Task<Step<bool>> RefreshLastCompletedAtAsync(AuditActor actor, string? taskId, string occurrenceId, DateTimeOffset now, CancellationToken ct)
    {
        if (taskId is null)
        {
            return false;
        }

        if (!(await occurrences.FindLatestCompletionAsync(taskId, ct).ConfigureAwait(false)).AsStep().TryGet(out var latest, out var latestFailure))
        {
            return latestFailure;
        }

        if (!(await tasks.FindAsync(taskId, ct).ConfigureAwait(false)).AsOptional().TryGet(out var task, out var taskFailure))
        {
            return taskFailure;
        }

        if (task is null || task.LastCompletedAt == latest.At)
        {
            return false;
        }

        var written = await tasks.SetLastCompletedAtAsync(taskId, latest.At, now, ct).ConfigureAwait(false);
        if (!written.AsStep().TryGet(out _, out var writeFailure))
        {
            return writeFailure;
        }

        var entry = TaskAudit.ForLastCompletedAt(actor, taskId, task.LastCompletedAt, latest.At, occurrenceId);
        return entry is null ? false : await RecordAsync(entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The value an occurrence snapshots when it becomes done (ADR-0011): the points a one-off task was recorded with, else the task's points, or
    /// the duration rule for a one-off task and for a task that no longer exists. Taken once, at completion.
    /// </summary>
    private async Task<Step<int>> PointsSnapshotAsync(Occurrence occurrence, CancellationToken ct)
    {
        if (occurrence.PointsOverride is { } chosen)
        {
            return chosen;
        }

        if (occurrence.TaskId is null)
        {
            return TaskPoints.DefaultForDuration(occurrence.DurationMinutesSnapshot);
        }

        if (!(await tasks.FindAsync(occurrence.TaskId, ct).ConfigureAwait(false)).AsOptional().TryGet(out var task, out var failure))
        {
            return failure;
        }

        return task?.Points ?? TaskPoints.DefaultForDuration(occurrence.DurationMinutesSnapshot);
    }

    private async Task<Step<OccurrenceWarning[]>> WarningsAsync(string? assigneeId, DateOnly day, CancellationToken ct)
    {
        if (assigneeId is null)
        {
            OccurrenceWarning[] none = [];
            return none;
        }

        if (!(await users.FindAsync(assigneeId, ct).ConfigureAwait(false)).AsOptional().TryGet(out var person, out var failure))
        {
            return failure;
        }

        OccurrenceWarning[] warnings = [.. OccurrenceRules.UnavailableWarnings(person, day)];
        return warnings;
    }

    private async Task<Step<bool>> RecordAsync(AuditEntry entry, CancellationToken ct)
    {
        var recorded = await audit.RecordAsync(entry, ct).ConfigureAwait(false);
        return recorded.Match<Step<bool>>(_ => true, error => error);
    }

    // ---- transactions

    private static async Task<TransactionOutcome<Step<T>>> InTransactionAsync<T>(Task<Step<T>> work)
    {
        var step = await work.ConfigureAwait(false);
        return step.IsSuccess ? TransactionOutcome.Commit(step) : TransactionOutcome.Abort(step);
    }

    private async Task<OneOf<TOut, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> RunAsync<T, TOut>(
        Func<CancellationToken, Task<Step<T>>> work, Func<T, TOut> map, CancellationToken cancellationToken)
    {
        var ran = await transactions.RunAsync(ct => InTransactionAsync(work(ct)), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<TOut, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>>(
            step => step.ToOneOf(map).Match<OneOf<TOut, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>>(
                value => value, notFound => notFound, invalid => invalid, conflict => conflict, missing => missing, error => error),
            conflict => conflict,
            error => error);
    }

    private Task<OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError>> RunViewAsync(
        Func<CancellationToken, Task<Step<(Context, Occurrence)>>> work, CancellationToken cancellationToken) =>
        RunAsync(work, pair => pair.Item1.ViewOf(pair.Item2), cancellationToken);
}
