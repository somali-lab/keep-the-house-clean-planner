using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Domain.Activation;

/// <summary>
/// The window a preview and an activation look at: from today to the end of the next cycle (the current and the next cycle, requirements 4.3).
/// </summary>
public sealed record ActivationWindow(DateOnly Today, int CurrentCycle, DateTimeOffset From, DateTimeOffset To)
{
    public static ActivationWindow Of(HouseholdSettings settings, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(zone);
        var today = DayKeys.Today(zone, now);
        var current = Cycles.CycleIndexFor(today, settings.CycleAnchorDate);
        return new ActivationWindow(
            today,
            current,
            DayKeys.FromDayKey(today, zone),
            DayKeys.FromDayKey(Cycles.CycleStart(current + 2, settings.CycleAnchorDate), zone));
    }
}

/// <summary>
/// The pure projection of an activation (port of <c>domain/activationPreview.ts</c>): which occurrences the replacement rule removes, which the
/// plan adds, which stay, and the token over all of it. The token is a SHA-256 over the visible result and over everything the result depends
/// on (the plan and its update time, the tasks and rooms the plan uses, the occurrence state of the window, the active plan, the settings that
/// matter and the two cycle documents), so a change in any of them between preview and activation makes the token differ.
/// </summary>
public static class ActivationPreviewer
{
    public static ActivationPreview Build(
        CyclePlan plan,
        string? activePlanId,
        HouseholdSettings settings,
        TimeZoneInfo zone,
        ActivationWindow window,
        IReadOnlyDictionary<string, HouseholdTask> tasks,
        IReadOnlyDictionary<string, string> roomNames,
        Cycle? currentCycle,
        Cycle? nextCycle,
        IReadOnlyList<Occurrence> existing)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(roomNames);
        ArgumentNullException.ThrowIfNull(existing);
        var anchor = settings.CycleAnchorDate;
        var cycleIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cycle in new[] { currentCycle, nextCycle })
        {
            if (cycle is not null)
            {
                cycleIndexById[cycle.Id] = cycle.Index;
            }
        }

        ActivationPreviewItem Item(Occurrence occurrence) => new(
            occurrence.Id,
            cycleIndexById.TryGetValue(occurrence.CycleId, out var index) ? index : Cycles.CycleIndexFor(DayKeys.ToDayKey(occurrence.PlannedDate, zone), anchor),
            occurrence.TaskId,
            occurrence.TaskNameSnapshot,
            DayKeys.ToDayKey(occurrence.Date, zone),
            occurrence.AssigneeId);

        var removedDocs = existing.Where(o => IsReplaceable(o, window)).ToList();
        var removedIds = removedDocs.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var removed = Sorted(removedDocs.Select(Item));
        var done = new List<ActivationPreviewItem>();
        var skipped = new List<ActivationPreviewItem>();
        var moved = new List<ActivationPreviewItem>();
        var adhoc = new List<ActivationPreviewItem>();
        foreach (var occurrence in existing.Where(o => !removedIds.Contains(o.Id)))
        {
            if (occurrence.Origin == OccurrenceOrigin.Adhoc)
            {
                adhoc.Add(Item(occurrence));
            }
            else if (occurrence.Status == OccurrenceStatus.Done)
            {
                done.Add(Item(occurrence));
            }
            else if (occurrence.Status == OccurrenceStatus.Skipped)
            {
                skipped.Add(Item(occurrence));
            }
            else if (occurrence.Date != occurrence.PlannedDate)
            {
                moved.Add(Item(occurrence));
            }
        }

        // The unique index is (cycle, task, planned day) among generated occurrences: the survivors with that key suppress an insert, skipped and
        // moved ones included; ad-hoc occurrences never occupy a slot.
        var occupied = existing
            .Where(o => o.Origin == OccurrenceOrigin.Generated && !removedIds.Contains(o.Id))
            .Select(o => $"{o.CycleId}:{o.TaskId ?? "none"}:{o.PlannedDate.ToUnixTimeMilliseconds()}")
            .ToHashSet(StringComparer.Ordinal);
        var added = new List<ActivationPreviewItem>();
        foreach (var (cycleIndex, cycle) in new[] { (window.CurrentCycle, currentCycle), (window.CurrentCycle + 1, nextCycle) })
        {
            var cycleKey = cycle?.Id ?? $"new:{cycleIndex}";
            foreach (var planned in OccurrencePlanner.Plan(plan.Slots, cycleIndex, settings, tasks, window.Today))
            {
                var instant = DayKeys.FromDayKey(planned.Day, zone).ToUnixTimeMilliseconds();
                if (!occupied.Add($"{cycleKey}:{planned.Task.Id}:{instant}"))
                {
                    continue;
                }

                added.Add(new ActivationPreviewItem(null, cycleIndex, planned.Task.Id, planned.Task.Name, planned.Day, planned.AssigneeId));
            }
        }

        var preview = new ActivationPreview(
            plan.Id,
            string.Empty,
            window.Today,
            removed,
            Sorted(added),
            new PreservedOccurrences(Sorted(done), Sorted(skipped), Sorted(moved), Sorted(adhoc)));
        return preview with { PreviewToken = Token(preview, plan, activePlanId, settings, tasks, roomNames, currentCycle, nextCycle, existing) };
    }

    /// <summary>Open, generated, on its planned day and inside the window: what an activation replaces.</summary>
    public static bool IsReplaceable(Occurrence occurrence, ActivationWindow window)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(window);
        return occurrence.Status == OccurrenceStatus.Open
            && occurrence.Origin == OccurrenceOrigin.Generated
            && occurrence.Date >= window.From && occurrence.Date < window.To
            && occurrence.Date == occurrence.PlannedDate;
    }

    private static List<ActivationPreviewItem> Sorted(IEnumerable<ActivationPreviewItem> items) =>
        [.. items
            .OrderBy(i => i.Date)
            .ThenBy(i => i.TaskName, StringComparer.Ordinal)
            .ThenBy(i => i.TaskId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(i => i.OccurrenceId ?? string.Empty, StringComparer.Ordinal)];

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Millis(DateTimeOffset instant) => instant.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private static string Item(ActivationPreviewItem i) =>
        string.Join("|", i.OccurrenceId, i.CycleIndex.ToString(CultureInfo.InvariantCulture), i.TaskId, i.TaskName, Day(i.Date), i.AssigneeId);

    private static string Token(
        ActivationPreview visible,
        CyclePlan plan,
        string? activePlanId,
        HouseholdSettings settings,
        IReadOnlyDictionary<string, HouseholdTask> tasks,
        IReadOnlyDictionary<string, string> roomNames,
        Cycle? currentCycle,
        Cycle? nextCycle,
        IReadOnlyList<Occurrence> existing)
    {
        var text = new StringBuilder();
        void Line(string value) => text.Append(value).Append('\n');
        Line("plan:" + visible.PlanId + ":" + Day(visible.AsOfDate));
        foreach (var (name, items) in new[]
        {
            ("removed", visible.Removed), ("added", visible.Added), ("done", visible.Preserved.Done),
            ("skipped", visible.Preserved.Skipped), ("moved", visible.Preserved.Moved), ("adhoc", visible.Preserved.Adhoc),
        })
        {
            Line(name + ":" + string.Join(";", items.Select(Item)));
        }

        Line("planState:" + plan.Name + "|" + Millis(plan.UpdatedAt));
        foreach (var slot in plan.Slots)
        {
            Line($"slot:{slot.TaskId}|{slot.WeekIndex}|{slot.Weekday}|{slot.AssigneeId}|{slot.SortOrder}");
        }

        var slotTasks = plan.Slots.Select(s => s.TaskId).ToHashSet(StringComparer.Ordinal);
        foreach (var task in tasks.Values.Where(t => slotTasks.Contains(t.Id)).OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            roomNames.TryGetValue(task.RoomId, out var room);
            Line($"task:{task.Id}|{task.Active}|{task.Name}|{task.DurationMinutes}|{task.RoomId}|{room}");
        }

        foreach (var occurrence in existing.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            Line(string.Join(
                "|",
                "occ:" + occurrence.Id,
                occurrence.CycleId,
                occurrence.PlanId,
                occurrence.TaskId,
                Millis(occurrence.Date),
                Millis(occurrence.PlannedDate),
                occurrence.AssigneeId,
                OccurrenceNames.ToWire(occurrence.Status),
                OccurrenceNames.ToWire(occurrence.Origin),
                Millis(occurrence.UpdatedAt)));
        }

        Line("active:" + activePlanId);
        Line($"settings:{settings.Timezone}|{Day(settings.CycleAnchorDate)}|{string.Join(";", settings.VacationRanges.Select(r => Day(r.From) + "~" + Day(r.To)))}");
        Line($"cycles:{currentCycle?.Id}|{nextCycle?.Id}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
