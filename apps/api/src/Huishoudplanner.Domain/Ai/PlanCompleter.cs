using System.Globalization;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Planning;

namespace Huishoudplanner.Domain.Ai;

/// <summary>
/// Completes a model's plan (port of <c>completeRequiredOccurrences</c> in <c>proposals.ts</c>). Small local models regularly return a valid
/// but incomplete slot array: their valid choices are kept and every missing recurring occurrence is filled in evenly over the 28-day
/// cycle. Tasks without a per-cycle count do not belong in the grid, so only what the model chose stays. A slot without assignee gets
/// the available person with the fewest minutes so far (within the daily maximum when that is possible).
/// </summary>
public static class PlanCompleter
{
    private const int CycleDays = 28;

    public static IReadOnlyList<CyclePlanSlot> CompleteRequiredOccurrences(PlanPromptPayload payload, IReadOnlyList<CyclePlanSlot> proposed)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(proposed);
        var completed = new List<CyclePlanSlot>();
        var userMinutes = payload.Users.ToDictionary(u => u.Id, _ => 0, StringComparer.Ordinal);
        var userDayMinutes = new Dictionary<string, int>(StringComparer.Ordinal);

        static string DayKey(string userId, int weekIndex, int weekday) =>
            string.Create(CultureInfo.InvariantCulture, $"{userId}:{weekIndex}:{weekday}");

        void AddMinutes(string assigneeId, int weekIndex, int weekday, int minutes)
        {
            userMinutes[assigneeId] = userMinutes.GetValueOrDefault(assigneeId) + minutes;
            var key = DayKey(assigneeId, weekIndex, weekday);
            userDayMinutes[key] = userDayMinutes.GetValueOrDefault(key) + minutes;
        }

        string? ChooseAssignee(int weekIndex, int weekday, int durationMinutes)
        {
            var present = payload.Users.Where(u => !u.UnavailableWeekdays.Contains(weekday)).ToList();
            var withinDailyLimit = present.Where(u =>
            {
                var limit = PlanValidator.IsWeekendDay(weekday) ? u.MaxDailyMinutes.Weekend : u.MaxDailyMinutes.Weekday;
                return userDayMinutes.GetValueOrDefault(DayKey(u.Id, weekIndex, weekday)) + durationMinutes <= limit;
            }).ToList();

            // Availability is a hard constraint. The daily maximum remains a warning, so an available person is still
            // preferable to an unassigned/shared slot.
            var candidates = withinDailyLimit.Count > 0 ? withinDailyLimit : present;
            return candidates.Count == 0
                ? null
                : candidates.OrderBy(u => userMinutes.GetValueOrDefault(u.Id)).First().Id;
        }

        CyclePlanSlot AssignConcreteUser(CyclePlanSlot slot, int durationMinutes) =>
            slot with { AssigneeId = slot.AssigneeId ?? ChooseAssignee(slot.WeekIndex, slot.Weekday, durationMinutes) };

        var taskIndex = 0;
        foreach (var task in payload.Tasks)
        {
            var index = taskIndex++;
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            if (task.PerCycle is null)
            {
                foreach (var slot in proposed.Where(s => s.TaskId == task.Id))
                {
                    if (!occupied.Add(PositionKey(slot.WeekIndex, slot.Weekday)))
                    {
                        continue;
                    }

                    var assigned = AssignConcreteUser(slot, task.DurationMinutes);
                    completed.Add(assigned);
                    if (assigned.AssigneeId is { } assignee)
                    {
                        AddMinutes(assignee, assigned.WeekIndex, assigned.Weekday, task.DurationMinutes);
                    }
                }

                continue;
            }

            var required = Math.Min(task.PerCycle.Value, CycleDays);
            if (required == 0)
            {
                continue;
            }

            foreach (var slot in proposed)
            {
                if (slot.TaskId != task.Id || completed.Count(c => c.TaskId == task.Id) >= required)
                {
                    continue;
                }

                if (!occupied.Add(PositionKey(slot.WeekIndex, slot.Weekday)))
                {
                    continue;
                }

                var assigned = AssignConcreteUser(slot, task.DurationMinutes);
                completed.Add(assigned);
                if (assigned.AssigneeId is { } assignee)
                {
                    AddMinutes(assignee, assigned.WeekIndex, assigned.Weekday, task.DurationMinutes);
                }
            }

            var offset = index * 3 % CycleDays;
            var ideal = Enumerable.Range(0, required).Select(i => (i * CycleDays / required + offset) % CycleDays);
            var all = Enumerable.Range(0, CycleDays).Select(i => (offset + i) % CycleDays);
            foreach (var position in ideal.Concat(all).Distinct())
            {
                if (occupied.Count >= required)
                {
                    break;
                }

                var weekIndex = position / 7;
                var weekday = PlanValidator.WeekdaysMondayFirst[position % 7];
                if (occupied.Contains(PositionKey(weekIndex, weekday)))
                {
                    continue;
                }

                var assigneeId = ChooseAssignee(weekIndex, weekday, task.DurationMinutes);
                occupied.Add(PositionKey(weekIndex, weekday));
                completed.Add(new CyclePlanSlot(task.Id, weekIndex, weekday, assigneeId));
                if (assigneeId is not null)
                {
                    AddMinutes(assigneeId, weekIndex, weekday, task.DurationMinutes);
                }
            }
        }

        return completed;
    }

    private static string PositionKey(int weekIndex, int weekday) => string.Create(CultureInfo.InvariantCulture, $"{weekIndex}:{weekday}");
}
