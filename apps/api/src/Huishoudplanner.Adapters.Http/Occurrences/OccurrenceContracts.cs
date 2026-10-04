using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using Huishoudplanner.Domain.Occurrences;

namespace Huishoudplanner.Adapters.Http.Occurrences;

// The request records below only document the bodies in OpenAPI; the bodies are read by OccurrenceRequestParser (Zod-style
// validation_error for a wrong type, null, malformed JSON or a bad day or id), never bound to these types.

/// <summary>
/// Completes an occurrence. The body may be left out. Work of someone else needs exactly one of <c>completedBy</c> (an active person,
/// the one credited) and <c>takeOver</c> (the actor does it and becomes the assignee); both together are refused.
/// </summary>
public sealed record CompleteOccurrenceRequest(
    [property: Description("An active person who performed the work; the points go to this person.")] string? CompletedBy,
    [property: Description("Only true is accepted: the actor performed the work and becomes the assignee.")] bool? TakeOver);

/// <summary>An administrator's correction of a completion. All three members are required.</summary>
public sealed record EditCompletionRequest(
    [property: Description("The day the completion belongs to, YYYY-MM-DD; a day outside the generated cycles is refused.")] string? Date,
    [property: Description("The moment of completion, an ISO 8601 instant with a time zone.")] string? CompletedAt,
    [property: Description("The person credited; must exist.")] string? CompletedBy);

/// <summary>Skips an open occurrence. The body may be left out.</summary>
public sealed record SkipOccurrenceRequest(
    [property: Description("At most 500 characters after trimming; empty means no reason.")] string? Reason);

/// <summary>Moves an open occurrence to another generated day.</summary>
public sealed record RescheduleOccurrenceRequest(
    [property: Description("The new day, YYYY-MM-DD.")] string? Date);

/// <summary>Assigns an open occurrence. <c>assigneeId</c> is required: an active person, or null for anyone.</summary>
public sealed record AssignOccurrenceRequest(
    [property: Description("An active person, or null for \"anyone\".")] string? AssigneeId);

/// <summary>A non-blocking remark on a write: <c>assignee_unavailable</c> with the person and the weekday in <c>details</c>.</summary>
public sealed record OccurrenceWarningResponse(string Code, string Message, IReadOnlyDictionary<string, object?> Details);

/// <summary>An occurrence as the API shows it: what actually happened on a day, next to what was planned.</summary>
public sealed record OccurrenceResponse(
    string Id,
    [property: Description("Null for a one-off task, whose name, duration and room live in the snapshot fields only.")] string? TaskId,
    string CycleId,
    [property: Description("The plan it was generated from; null for ad-hoc occurrences.")] string? PlanId,
    [property: Description("The day it sits on now, YYYY-MM-DD.")] string Date,
    [property: Description("The day of its slot, YYYY-MM-DD; kept when the occurrence is moved.")] string PlannedDate,
    string? AssigneeId,
    [property: Description("open, done or skipped.")] string Status,
    [property: Description("open or skipped: where an undo goes back to; null while not done.")] string? StatusBeforeCompletion,
    DateTimeOffset? CompletedAt,
    [property: Description("The person credited.")] string? CompletedBy,
    string? SkipReason,
    int DurationMinutesSnapshot,
    string TaskNameSnapshot,
    string? RoomIdSnapshot,
    string? RoomNameSnapshot,
    [property: Description("generated or adhoc.")] string Origin,
    [property: Description("Created directly in the done state: no planned state to return to.")] bool RecordedDone,
    [property: Description("The idempotency key of the creating request.")] string? RequestId,
    [property: Description("The points of this execution, fixed when it became done; null while not done.")] int? PointsSnapshot,
    [property: Description("The points a one-off task was recorded with; null means the duration rule.")] int? PointsOverride,
    [property: Description("The assignee frozen when work of an ended planned week first changed hands; null means the assignee.")] string? PeriodOwnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    [property: Description("Open and dated before today.")] bool IsOverdue,
    [property: Description("The planned day when the occurrence sits on another day, else null.")] string? MovedFrom,
    [property: Description("The cycle the day belongs to (negative before the anchor).")] int CycleIndex,
    [property: Description("The week of the cycle, 0 to 3.")] int WeekIndex,
    [property: Description("Only on the answers of reschedule and assignment: the non-blocking warnings (empty when there are none).")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OccurrenceWarningResponse>? Warnings = null)
{
    internal static OccurrenceResponse From(OccurrenceView view, IReadOnlyList<OccurrenceWarning>? warnings = null)
    {
        var o = view.Occurrence;
        return new OccurrenceResponse(
            o.Id,
            o.TaskId,
            o.CycleId,
            o.PlanId,
            Day(view.Date),
            Day(view.PlannedDate),
            o.AssigneeId,
            OccurrenceNames.ToWire(o.Status),
            o.StatusBeforeCompletion is { } before ? OccurrenceNames.ToWire(before) : null,
            o.CompletedAt,
            o.CompletedBy,
            o.SkipReason,
            o.DurationMinutesSnapshot,
            o.TaskNameSnapshot,
            o.RoomIdSnapshot,
            o.RoomNameSnapshot,
            OccurrenceNames.ToWire(o.Origin),
            o.RecordedDone,
            o.RequestId,
            o.PointsSnapshot,
            o.PointsOverride,
            o.PeriodOwnerId,
            o.CreatedAt,
            o.UpdatedAt,
            view.IsOverdue,
            view.MovedFrom is { } moved ? Day(moved) : null,
            view.CycleIndex,
            view.WeekIndex,
            warnings?.Select(w => new OccurrenceWarningResponse(w.Code, w.Message, w.Details)).ToList());
    }

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>One page of occurrences in the display order (day, task name, id; reversed for <c>order=desc</c>); <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
public sealed record OccurrenceListResponse(IReadOnlyList<OccurrenceResponse> Items, string? NextCursor);

/// <summary>The answer of a permanent deletion.</summary>
public sealed record DeleteOccurrenceResponse(bool Deleted);
