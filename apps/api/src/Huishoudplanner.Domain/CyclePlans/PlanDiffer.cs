namespace Huishoudplanner.Domain.CyclePlans;

/// <summary>Where a slot sits: week, weekday (0=Sunday..6=Saturday) and assignee.</summary>
public sealed record DiffPosition(int WeekIndex, int Weekday, string? AssigneeId);

/// <summary>The task fields a plan diff shows; a task that no longer exists shows as its id with no room and no minutes.</summary>
public sealed record PlanTaskInfo(string Name, string? RoomName, int DurationMinutes);

/// <summary>A slot that exists in only one of the two plans.</summary>
public sealed record DiffSlot(string TaskId, string TaskName, string? RoomName, int DurationMinutes, DiffPosition Position);

/// <summary>A slot of the same task that sits elsewhere, or with another person, in the other plan.</summary>
public sealed record MovedSlot(string TaskId, string TaskName, string? RoomName, int DurationMinutes, DiffPosition From, DiffPosition To);

public sealed record PlanDiff(IReadOnlyList<DiffSlot> Added, IReadOnlyList<DiffSlot> Removed, IReadOnlyList<MovedSlot> Moved, int Unchanged);

/// <summary>Port of <c>domain/planDiff.ts</c>.</summary>
public static class PlanDiffer
{
    /// <summary>
    /// Compares two plans per task. Identical slots are unchanged; a slot on the same day with another assignee, or any remaining slot
    /// that can be paired in cycle order, counts as moved; what is left is added (only in <paramref name="after"/>) or removed
    /// (only in <paramref name="before"/>).
    /// </summary>
    public static PlanDiff Diff(IReadOnlyList<CyclePlanSlot> before, IReadOnlyList<CyclePlanSlot> after, IReadOnlyDictionary<string, PlanTaskInfo> tasks)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(tasks);
        var added = new List<DiffSlot>();
        var removed = new List<DiffSlot>();
        var moved = new List<MovedSlot>();
        var unchanged = 0;

        foreach (var taskId in before.Concat(after).Select(s => s.TaskId).Distinct(StringComparer.Ordinal))
        {
            var info = tasks.TryGetValue(taskId, out var known) ? known : new PlanTaskInfo(taskId, null, 0);
            var remainingBefore = Ordered(before, taskId);
            var remainingAfter = Ordered(after, taskId);

            // 1. identical slots
            remainingAfter = [.. remainingAfter.Where(slot =>
            {
                var index = remainingBefore.FindIndex(b => SameDay(b, slot) && b.AssigneeId == slot.AssigneeId);
                if (index == -1)
                {
                    return true;
                }

                remainingBefore.RemoveAt(index);
                unchanged++;
                return false;
            })];

            // 2. same day, other assignee
            remainingAfter = [.. remainingAfter.Where(slot =>
            {
                var index = remainingBefore.FindIndex(b => SameDay(b, slot));
                if (index == -1)
                {
                    return true;
                }

                moved.Add(Move(taskId, info, remainingBefore[index], slot));
                remainingBefore.RemoveAt(index);
                return false;
            })];

            // 3. pair what is left, in cycle order
            while (remainingBefore.Count > 0 && remainingAfter.Count > 0)
            {
                moved.Add(Move(taskId, info, remainingBefore[0], remainingAfter[0]));
                remainingBefore.RemoveAt(0);
                remainingAfter.RemoveAt(0);
            }

            added.AddRange(remainingAfter.Select(s => ToSlot(taskId, info, s)));
            removed.AddRange(remainingBefore.Select(s => ToSlot(taskId, info, s)));
        }

        return new PlanDiff(added, removed, moved, unchanged);
    }

    private static List<CyclePlanSlot> Ordered(IReadOnlyList<CyclePlanSlot> slots, string taskId) =>
        [.. slots.Where(s => s.TaskId == taskId).OrderBy(s => s.WeekIndex).ThenBy(s => CyclePlanSlots.MondayFirst(s.Weekday))];

    private static bool SameDay(CyclePlanSlot a, CyclePlanSlot b) => a.WeekIndex == b.WeekIndex && a.Weekday == b.Weekday;

    private static DiffPosition Position(CyclePlanSlot slot) => new(slot.WeekIndex, slot.Weekday, slot.AssigneeId);

    private static DiffSlot ToSlot(string taskId, PlanTaskInfo info, CyclePlanSlot slot) =>
        new(taskId, info.Name, info.RoomName, info.DurationMinutes, Position(slot));

    private static MovedSlot Move(string taskId, PlanTaskInfo info, CyclePlanSlot from, CyclePlanSlot to) =>
        new(taskId, info.Name, info.RoomName, info.DurationMinutes, Position(from), Position(to));
}
