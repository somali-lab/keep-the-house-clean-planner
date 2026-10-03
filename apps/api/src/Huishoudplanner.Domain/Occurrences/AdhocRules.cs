using System.Globalization;
using System.Text.RegularExpressions;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Limits;

namespace Huishoudplanner.Domain.Occurrences;

/// <summary>What a repeated request key must match to count as the same request (<c>RequestIdentity</c> of the Node server).</summary>
/// <param name="TaskId">The existing task, or <see langword="null"/> for a one-off task (matched on its snapshot name).</param>
public sealed record AdhocIdentity(string? TaskId, string Name, DateOnly Date, bool Done);

/// <summary>
/// The rules of the extra executions and one-off tasks (requirements 4.4, ADR-0009), ported from <c>domain/occurrences.ts</c>: the shape of
/// a request, what recorded work needs, when a repeated key replays and when it conflicts, and when recorded work can be retracted.
/// Pure: the use case reads and writes, this decides.
/// </summary>
public static partial class AdhocRules
{
    public const int MaxNameLength = 120;

    public const string TaskAlreadyPlanned = "task_already_planned";

    public const string InvalidRequestKey = "invalid_request_key";

    /// <summary>The client idempotency key: 16 to 64 characters of letters, digits, underscore and hyphen (<c>requestKeySchema</c>).</summary>
    public static bool IsRequestKey(string? key) => key is not null && RequestKey().IsMatch(key);

    /// <summary>The shape of an extra execution request; <see langword="null"/> when it is valid. Reads nothing.</summary>
    public static ValidationErrors? Validate(ExtraExecutionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        CheckId("taskId", command.TaskId, errors);
        CheckCommon(command.Date, command.Assignee, command.RequestId, errors);
        return errors.Count == 0 ? null : new ValidationErrors(errors);
    }

    /// <summary>The shape of a one-off request (the name counts trimmed); <see langword="null"/> when it is valid. Reads nothing.</summary>
    public static ValidationErrors? Validate(OneOffCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var length = command.Name.Trim().Length;
        if (length is < 1 or > MaxNameLength)
        {
            errors["name"] = [string.Create(CultureInfo.InvariantCulture, $"must be 1 to {MaxNameLength} characters")];
        }

        if (command.RoomId is not null)
        {
            CheckId("roomId", command.RoomId, errors);
        }

        if (command.DurationMinutes < 1)
        {
            errors["durationMinutes"] = ["must be at least 1"];
        }

        if (command.Points is { } points && (points < TaskPoints.Min || points > TaskPoints.Max))
        {
            errors["points"] = [string.Create(CultureInfo.InvariantCulture, $"must be a whole number from {TaskPoints.Min} to {TaskPoints.Max}")];
        }

        CheckCommon(command.Date, command.Assignee, command.RequestId, errors);
        return errors.Count == 0 ? null : new ValidationErrors(errors);
    }

    /// <summary>
    /// Recorded work (<c>done</c>) must be for today and by a person: "anyone" did not do it. <see langword="null"/> when the request is
    /// not recorded work or satisfies both.
    /// </summary>
    public static ValidationErrors? CheckRecordedWork(bool done, DateOnly date, DateOnly today, Tasks.AssigneeChoice? assignee)
    {
        if (!done)
        {
            return null;
        }

        if (date != today)
        {
            return ValidationErrors.For("date", "done_requires_today");
        }

        return assignee is { UserId: null } ? ValidationErrors.For("assigneeId", "done_requires_person") : null;
    }

    /// <summary>
    /// The assignee of a new ad-hoc occurrence: what the request says (<see langword="null"/> id = anyone), else the actor when it is recorded
    /// as done, else <paramref name="fallback"/> (the task's default assignee of an extra execution, nobody for a one-off task).
    /// </summary>
    public static string? ResolveAssignee(Tasks.AssigneeChoice? requested, bool done, string actorId, string? fallback) =>
        requested is not null ? requested.UserId : done ? actorId : fallback;

    /// <summary>
    /// Whether the stored occurrence is the one the repeated request asks for: the same task (a one-off task: the same name), the same day
    /// and the same recorded-done flag. Anything else with the same key is an <c>idempotency_key_conflict</c>.
    /// </summary>
    public static bool IsReplay(Occurrence existing, AdhocIdentity identity, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(zone);
        var sameTarget = identity.TaskId is { } taskId
            ? OccurrenceRules.Same(existing.TaskId, taskId)
            : existing.TaskId is null && string.Equals(existing.TaskNameSnapshot, identity.Name, StringComparison.Ordinal);
        return sameTarget
            && DayKeys.ToDayKey(existing.PlannedDate, zone) == identity.Date
            && existing.RecordedDone == identity.Done;
    }

    /// <summary>Only recorded extra work (an ad-hoc occurrence created done) can be retracted.</summary>
    public static bool IsRetractable(Occurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return occurrence.Origin == OccurrenceOrigin.Adhoc && occurrence.RecordedDone && occurrence.Status == OccurrenceStatus.Done;
    }

    /// <summary>The warning of planning or recording a task on a day where it already has an open occurrence.</summary>
    public static OccurrenceWarning AlreadyPlanned(string taskId, DateOnly date) => new(
        TaskAlreadyPlanned,
        "This task is already planned on that day",
        new Dictionary<string, object?> { ["taskId"] = taskId, ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });

    private static void CheckCommon(DateOnly date, Tasks.AssigneeChoice? assignee, string? requestId, Dictionary<string, string[]> errors)
    {
        if (!OccurrenceRules.IsSupportedDay(date))
        {
            errors["date"] = ["out_of_range"];
        }

        if (assignee?.UserId is { } person)
        {
            CheckId("assigneeId", person, errors);
        }

        if (requestId is not null && !IsRequestKey(requestId))
        {
            errors["requestId"] = [InvalidRequestKey];
        }
    }

    private static void CheckId(string field, string id, Dictionary<string, string[]> errors)
    {
        if (!OccurrenceRules.IsId(id))
        {
            errors[field] = ["invalid_object_id"];
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{16,64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex RequestKey();
}
