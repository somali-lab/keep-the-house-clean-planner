using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Users;

namespace Huishoudplanner.Domain.Occurrences;

/// <summary>
/// The rules of the occurrence actions (requirements 4.4), ported from <c>domain/occurrences.ts</c> and <c>routes/occurrences.ts</c>: which
/// choices a completion needs, who owns a period, which warnings a move carries. Pure: the use case reads and writes, this decides.
/// </summary>
public static class OccurrenceRules
{
    public const int MaxSkipReasonLength = 500;

    public const string AssigneeUnavailable = "assignee_unavailable";

    /// <summary>The API id format: 24 hexadecimal characters.</summary>
    public static bool IsId(string? id) =>
        id is { Length: 24 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    /// <summary>
    /// The shape of a completion request: a malformed <c>completedBy</c> and both choices at once (<c>completion_choice_conflict</c>) are
    /// refused before anything is read. <see langword="null"/> when it is valid.
    /// </summary>
    public static ValidationErrors? Validate(CompleteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CompletedBy is not null && !IsId(command.CompletedBy))
        {
            return ValidationErrors.For("completedBy", "invalid_object_id");
        }

        return command.CompletedBy is not null && command.TakeOver
            ? ValidationErrors.For("completedBy", "completion_choice_conflict")
            : null;
    }

    /// <summary>A reason is trimmed and at most 500 characters; an empty one is no reason. <see langword="null"/> in the error slot when it is valid.</summary>
    public static (string? Reason, ValidationErrors? Error) NormaliseSkipReason(string? reason)
    {
        var trimmed = reason?.Trim();
        return trimmed is { Length: > MaxSkipReasonLength }
            ? (null, ValidationErrors.For("reason", "must be at most 500 characters"))
            : (string.IsNullOrEmpty(trimmed) ? null : trimmed, null);
    }

    /// <summary>
    /// Work of someone else needs an explicit choice (ADR-0011): either a named person or a take over. Unassigned work and work of the actor
    /// itself default to the actor.
    /// </summary>
    public static bool NeedsCompletionChoice(Occurrence current, string actorId, CompleteCommand command)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);
        var assignedToSomeoneElse = current.AssigneeId is not null && !Same(current.AssigneeId, actorId);
        return assignedToSomeoneElse && !command.TakeOver && command.CompletedBy is null;
    }

    /// <summary>
    /// Freezes the period owner (ADR-0012): the first time work whose planned week has already ended is assigned, claimed, taken over or
    /// completed, the assignee of that moment (before the change) is remembered as the person the week's bonus is counted for. Recorded work has
    /// no plan, and an owner that is already frozen stays. Returns <paramref name="changed"/> with the owner set, or unchanged.
    /// </summary>
    public static Occurrence FreezePeriodOwner(Occurrence current, Occurrence changed, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(zone);
        if (current.HasPeriodOwner || current.RecordedDone)
        {
            return changed;
        }

        var week = Period.WeekOf(DayKeys.ToDayKey(current.PlannedDate, zone));
        return week.HasEnded(DayKeys.Today(zone, now))
            ? changed with { PeriodOwnerId = current.AssigneeId, PeriodOwnerFrozen = true }
            : changed;
    }

    /// <summary>Moving or assigning onto a day the assignee is unavailable is allowed, but reported.</summary>
    public static IReadOnlyList<OccurrenceWarning> UnavailableWarnings(User? assignee, DateOnly day)
    {
        var weekday = DayKeys.WeekdaySun0(day);
        if (assignee is null || !assignee.UnavailableWeekdays.Contains(weekday))
        {
            return [];
        }

        return
        [
            new OccurrenceWarning(
                AssigneeUnavailable,
                "Assignee is not available on this weekday",
                new Dictionary<string, object?> { ["userId"] = assignee.Id, ["weekday"] = weekday }),
        ];
    }

    public static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>An occurrence after an action, the audit action it is recorded as and its <c>meta</c> (without the occurrence context, which the audit adds).</summary>
public sealed record OccurrenceTransition(Occurrence After, AuditAction Action, AuditObject? Meta);

/// <summary>
/// The occurrence actions as pure state transitions, with the history exactly as the Node server writes it (<c>meta</c> keys and values).
/// The status guards (<c>invalid_transition</c>) belong to the use case, which reads the state; these functions assume a legal source state.
/// </summary>
public static class OccurrenceTransitions
{
    public static OccurrenceTransition Complete(
        Occurrence current, string actorId, string completedBy, bool takeOver, int pointsSnapshot, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(current);
        var wasAssignee = OccurrenceRules.Same(current.AssigneeId, completedBy);
        var claimed = current.AssigneeId is null;
        var reassigned = takeOver && !OccurrenceRules.Same(current.AssigneeId, actorId);
        var next = current with
        {
            Status = OccurrenceStatus.Done,
            StatusBeforeCompletion = current.Status,
            CompletedAt = now,
            CompletedBy = completedBy,
            PointsSnapshot = pointsSnapshot,

            // Completing unassigned work claims it; taking over assigned work transfers it to the actor.
            AssigneeId = claimed || takeOver ? completedBy : current.AssigneeId,
            UpdatedAt = now,
        };
        var meta = new List<KeyValuePair<string, AuditValue>>
        {
            new("completedBy", new AuditObjectId(completedBy)),
            new("wasAssignee", wasAssignee),
        };
        if (claimed)
        {
            meta.Add(new("claimed", true));
        }

        if (reassigned)
        {
            meta.Add(new("takenOver", true));
            meta.Add(new("previousAssigneeId", current.AssigneeId is { } previous ? new AuditObjectId(previous) : AuditNull.Instance));
        }

        return new(OccurrenceRules.FreezePeriodOwner(current, next, zone, now), AuditAction.Complete, new AuditObject(meta));
    }

    /// <summary>Back to the status before the completion; the points snapshot belongs to one completion, so a later check-off takes the value of that moment.</summary>
    public static OccurrenceTransition Uncomplete(Occurrence current, DateTimeOffset now) =>
        new(
            current with
            {
                Status = current.StatusBeforeCompletion ?? OccurrenceStatus.Open,
                StatusBeforeCompletion = null,
                CompletedAt = null,
                CompletedBy = null,
                PointsSnapshot = null,
                UpdatedAt = now,
            },
            AuditAction.Uncomplete,
            null);

    public static OccurrenceTransition EditCompletion(
        Occurrence current, EditCompletionCommand command, string? newCycleId, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);
        return new(
            current with
            {
                Date = DayKeys.FromDayKey(command.Date, zone),
                CompletedAt = command.CompletedAt,
                CompletedBy = command.CompletedBy,
                CycleId = newCycleId ?? current.CycleId,
                UpdatedAt = now,
            },
            AuditAction.Update,
            AuditObject.Of(("correction", "completion")));
    }

    public static OccurrenceTransition Skip(Occurrence current, string? reason, DateTimeOffset now) =>
        new(current with { Status = OccurrenceStatus.Skipped, SkipReason = reason, UpdatedAt = now }, AuditAction.Skip, null);

    /// <summary>Drag to another day: <c>plannedDate</c> stays so drift is measurable and <c>cycleId</c> follows the new date.</summary>
    public static OccurrenceTransition Reschedule(Occurrence current, DateOnly to, string cycleId, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(current);
        var from = DayKeys.ToDayKey(current.Date, zone);
        return new(
            current with { Date = DayKeys.FromDayKey(to, zone), CycleId = cycleId, UpdatedAt = now },
            AuditAction.Reschedule,
            AuditObject.Of(("from", DayKey(from)), ("to", DayKey(to))));
    }

    /// <summary>Assigning the person who already has it changes nothing, so it freezes nothing either.</summary>
    public static OccurrenceTransition Assign(Occurrence current, string? assigneeId, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(current);
        var same = OccurrenceRules.Same(current.AssigneeId, assigneeId);
        var next = current with { AssigneeId = assigneeId, UpdatedAt = now };
        return new(same ? next : OccurrenceRules.FreezePeriodOwner(current, next, zone, now), AuditAction.Assign, null);
    }

    /// <summary>A claim is an assignment of the actor, audited as <c>assign</c> with <c>meta.claim</c>.</summary>
    public static OccurrenceTransition Claim(Occurrence current, string actorId, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(current);
        var next = current with { AssigneeId = actorId, UpdatedAt = now };
        return new(OccurrenceRules.FreezePeriodOwner(current, next, zone, now), AuditAction.Assign, AuditObject.Of(("claim", true)));
    }

    private static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
