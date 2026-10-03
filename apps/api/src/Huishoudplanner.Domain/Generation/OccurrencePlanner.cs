using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Domain.Generation;

/// <summary>An occurrence a plan expects on a day of a cycle: the one source for the dates that generation, the reconciliation and the activation preview use.</summary>
public sealed record PlannedOccurrence(int CycleIndex, DateOnly Day, HouseholdTask Task, string? AssigneeId);

/// <summary>
/// The pure generation rules (port of <c>plannedOccurrences</c> and <c>isInVacation</c> of <c>domain/generation.ts</c>): which occurrences
/// the slots of a plan produce in a cycle, and what a generated occurrence snapshots. Every date is a day key computed with the calendar
/// helpers, never with local date arithmetic, so a daylight saving change cannot move an occurrence.
/// </summary>
public static class OccurrencePlanner
{
    public static bool IsInVacation(DateOnly day, IReadOnlyList<VacationRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        return ranges.Any(r => r.From <= day && day <= r.To);
    }

    /// <summary>
    /// The occurrences the <paramref name="slots"/> of a plan produce in the cycle, in slot order. Skipped: slots of a task that is unknown or
    /// inactive, vacation days, and days before <paramref name="today"/> (so a plan activated midway does not create instantly overdue work).
    /// </summary>
    public static IReadOnlyList<PlannedOccurrence> Plan(
        IReadOnlyList<CyclePlanSlot> slots,
        int cycleIndex,
        HouseholdSettings settings,
        IReadOnlyDictionary<string, HouseholdTask> tasks,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tasks);
        var start = Cycles.CycleStart(cycleIndex, settings.CycleAnchorDate);
        var planned = new List<PlannedOccurrence>();
        foreach (var slot in slots)
        {
            if (!tasks.TryGetValue(slot.TaskId, out var task) || !task.Active)
            {
                continue;
            }

            var day = Cycles.SlotDate(start, slot.WeekIndex, slot.Weekday);
            if (day < today || IsInVacation(day, settings.VacationRanges))
            {
                continue;
            }

            planned.Add(new PlannedOccurrence(cycleIndex, day, task, slot.AssigneeId));
        }

        return planned;
    }

    /// <summary>
    /// The occurrence to store for a planned one: open, on its planned day at local midnight, with the name, duration and room of the task
    /// as they are now (ADR-0011). <paramref name="roomNames"/> maps room ids to names; a task whose room is gone snapshots no room name.
    /// </summary>
    public static NewGeneratedOccurrence ToDraft(
        PlannedOccurrence planned,
        string cycleId,
        string planId,
        TimeZoneInfo zone,
        IReadOnlyDictionary<string, string> roomNames,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(roomNames);
        return new NewGeneratedOccurrence(
            planned.Task.Id,
            cycleId,
            planId,
            DayKeys.FromDayKey(planned.Day, zone),
            planned.AssigneeId,
            planned.Task.DurationMinutes,
            planned.Task.Name,
            planned.Task.RoomId,
            roomNames.GetValueOrDefault(planned.Task.RoomId),
            now);
    }
}
