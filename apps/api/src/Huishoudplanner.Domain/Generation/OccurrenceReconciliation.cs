using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Occurrences;

namespace Huishoudplanner.Domain.Generation;

/// <summary>
/// When do the upcoming occurrences no longer match the active plan, so that a generation run replaces them (port of
/// <c>upcomingOccurrencesNeedReplacement</c> in <c>domain/generation.ts</c>)? The typical cause is an anchor date that moved, or slots saved
/// without synchronising. Only generated occurrences occupy a slot (ADR-0009); only open, untouched ones may be replaced.
/// </summary>
public static class OccurrenceReconciliation
{
    /// <summary>The identity of a slot occurrence: the task and the planned instant (a one-off task has no task id).</summary>
    public static string KeyOf(string? taskId, DateTimeOffset plannedDate) => $"{taskId ?? "none"}:{plannedDate.ToUnixTimeMilliseconds()}";

    /// <summary>
    /// Replaceable work is open, generated, still on its planned day and not before today: anything done, skipped, dragged or ad hoc records
    /// something that happened and stays (requirements 4.3).
    /// </summary>
    public static bool IsReplaceable(Occurrence occurrence, DateTimeOffset todayStart)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return occurrence.Origin == OccurrenceOrigin.Generated
            && occurrence.Status == OccurrenceStatus.Open
            && occurrence.Date >= todayStart
            && occurrence.Date == occurrence.PlannedDate;
    }

    /// <param name="expected">What the active plan produces in the current and the next cycle (<see cref="OccurrencePlanner.Plan"/>).</param>
    /// <param name="existingGenerated">The generated occurrences planned from today to the end of the next cycle.</param>
    /// <param name="planId">The active plan.</param>
    public static bool NeedsReplacement(
        IReadOnlyList<PlannedOccurrence> expected,
        IReadOnlyList<Occurrence> existingGenerated,
        string planId,
        DateTimeOffset todayStart,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(existingGenerated);
        ArgumentNullException.ThrowIfNull(zone);
        var expectedAssignees = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var planned in expected)
        {
            expectedAssignees[KeyOf(planned.Task.Id, DayKeys.FromDayKey(planned.Day, zone))] = planned.AssigneeId;
        }

        var generated = existingGenerated.Where(o => o.Origin == OccurrenceOrigin.Generated).ToList();
        var existingKeys = generated.Select(o => KeyOf(o.TaskId, o.PlannedDate)).ToHashSet(StringComparer.Ordinal);
        if (expectedAssignees.Keys.Any(key => !existingKeys.Contains(key)))
        {
            return true;
        }

        return generated.Any(occurrence =>
        {
            if (!IsReplaceable(occurrence, todayStart))
            {
                return false;
            }

            return !expectedAssignees.TryGetValue(KeyOf(occurrence.TaskId, occurrence.PlannedDate), out var assignee)
                || occurrence.PlanId != planId
                || occurrence.AssigneeId != assignee;
        });
    }
}
