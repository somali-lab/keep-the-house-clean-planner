using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Domain.Planning;

/// <summary>
/// Plan validation (port of <c>packages/shared/src/validation/plan.ts</c>): the single authority for slot errors,
/// interval counts, availability, budget warnings and workload summaries. Pure; ids are strings. The order of the
/// error and warning lists is part of the contract (<c>Vectors/validation.json</c>).
/// </summary>
public static class PlanValidator
{
    public const int PlanWeeks = 4;

    /// <summary>Monday-first display order of the 0=Sunday..6=Saturday weekdays.</summary>
    public static IReadOnlyList<int> WeekdaysMondayFirst { get; } = [1, 2, 3, 4, 5, 6, 0];

    public static bool IsWeekendDay(int weekday) => weekday is 0 or 6;

    /// <summary>The daily maximum of a user on a weekday (weekend or Monday-Friday value).</summary>
    public static int BudgetFor(PlanUser user, int weekday) =>
        IsWeekendDay(weekday) ? user.MaxDailyMinutes.Weekend : user.MaxDailyMinutes.Weekday;

    public static PlanValidation Validate(
        PlanDraft plan,
        IReadOnlyList<PlanTask> tasks,
        IReadOnlyList<PlanUser> users,
        IReadOnlyList<Interval> intervals)
    {
        var errors = new List<PlanIssue>();
        var warnings = new List<PlanIssue>();

        var taskById = Index(tasks, t => t.Id);
        var userById = Index(users, u => u.Id);
        var intervalByKey = Index(intervals, i => i.Key);
        var activeUsers = users.Where(u => u.Active).ToList();

        var placed = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenTaskDay = new HashSet<string>(StringComparer.Ordinal);
        var dayMinutes = new Dictionary<(int, int), Dictionary<string, int>>();
        var dayUnassigned = new Dictionary<(int, int), int>();

        for (var slotIndex = 0; slotIndex < plan.Slots.Count; slotIndex++)
        {
            var slot = plan.Slots[slotIndex];
            var index = slotIndex;
            PlanIssue Issue(string code, string? userId = null) =>
                new(code, SlotIndex: index, TaskId: slot.TaskId, UserId: userId, WeekIndex: slot.WeekIndex, Weekday: slot.Weekday);

            var positionOk = true;
            if (slot.WeekIndex is < 0 or >= PlanWeeks)
            {
                errors.Add(Issue(PlanErrorCodes.WeekIndexOutOfRange));
                positionOk = false;
            }

            if (slot.Weekday is < 0 or > 6)
            {
                errors.Add(Issue(PlanErrorCodes.WeekdayOutOfRange));
                positionOk = false;
            }

            taskById.TryGetValue(slot.TaskId, out var task);
            if (task is null)
            {
                errors.Add(Issue(PlanErrorCodes.UnknownTask));
            }
            else if (!task.Active)
            {
                errors.Add(Issue(PlanErrorCodes.InactiveTask));
            }

            var assigneeOk = true;
            if (slot.AssigneeId is not null)
            {
                if (!userById.TryGetValue(slot.AssigneeId, out var user))
                {
                    errors.Add(Issue(PlanErrorCodes.UnknownUser, slot.AssigneeId));
                    assigneeOk = false;
                }
                else if (!user.Active)
                {
                    errors.Add(Issue(PlanErrorCodes.InactiveUser, slot.AssigneeId));
                    assigneeOk = false;
                }
                else if (positionOk && user.UnavailableWeekdays.Contains(slot.Weekday))
                {
                    errors.Add(Issue(PlanErrorCodes.AssigneeUnavailable, slot.AssigneeId));
                }
            }

            if (!positionOk)
            {
                continue;
            }

            var key = (slot.WeekIndex, slot.Weekday);
            if (!seenTaskDay.Add($"{slot.TaskId}@{slot.WeekIndex}:{slot.Weekday}"))
            {
                errors.Add(Issue(PlanErrorCodes.DuplicateTaskDay));
            }

            if (task is null)
            {
                continue;
            }

            placed[task.Id] = placed.GetValueOrDefault(task.Id) + 1;

            if (slot.AssigneeId is null)
            {
                dayUnassigned[key] = dayUnassigned.GetValueOrDefault(key) + task.DurationMinutes;
            }
            else if (assigneeOk)
            {
                if (!dayMinutes.TryGetValue(key, out var perUser))
                {
                    dayMinutes[key] = perUser = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                perUser[slot.AssigneeId] = perUser.GetValueOrDefault(slot.AssigneeId) + task.DurationMinutes;
            }
        }

        // Per-task placed/required: every active task, plus inactive ones that still have slots.
        var summaryTasks = new List<TaskSummary>();
        foreach (var task in tasks)
        {
            var count = placed.GetValueOrDefault(task.Id);
            if (!task.Active && count == 0)
            {
                continue;
            }

            int? required = intervalByKey.TryGetValue(task.IntervalKey, out var interval) ? interval.PerCycle : null;
            summaryTasks.Add(new TaskSummary(task.Id, count, required));
            if (task.Active && required is not null && count != required)
            {
                warnings.Add(new PlanIssue(PlanWarningCodes.IntervalMismatch, TaskId: task.Id, Placed: count, Required: required));
            }
        }

        var days = new List<DaySummary>();
        var weeks = new List<WeekSummary>();
        for (var weekIndex = 0; weekIndex < PlanWeeks; weekIndex++)
        {
            var weekTotals = activeUsers.ToDictionary(u => u.Id, _ => 0, StringComparer.Ordinal);
            var weekdayTotals = activeUsers.ToDictionary(u => u.Id, _ => 0, StringComparer.Ordinal);
            var weekendTotals = activeUsers.ToDictionary(u => u.Id, _ => 0, StringComparer.Ordinal);
            var weekUnassigned = 0;
            foreach (var weekday in WeekdaysMondayFirst)
            {
                dayMinutes.TryGetValue((weekIndex, weekday), out var perUser);
                var periodTotals = IsWeekendDay(weekday) ? weekendTotals : weekdayTotals;
                foreach (var user in activeUsers)
                {
                    periodTotals[user.Id] += perUser?.GetValueOrDefault(user.Id) ?? 0;
                }
            }

            foreach (var user in activeUsers)
            {
                foreach (var period in new[] { BudgetPeriod.Weekday, BudgetPeriod.Weekend })
                {
                    var minutes = (period == BudgetPeriod.Weekday ? weekdayTotals : weekendTotals)[user.Id];
                    var budget = period == BudgetPeriod.Weekday ? user.DailyBudgetMinutes.Weekday : user.DailyBudgetMinutes.Weekend;
                    if (minutes > budget)
                    {
                        warnings.Add(new PlanIssue(
                            PlanWarningCodes.OverBudget, UserId: user.Id, WeekIndex: weekIndex, Period: period, Minutes: minutes, Budget: budget));
                    }
                }
            }

            foreach (var weekday in WeekdaysMondayFirst)
            {
                dayMinutes.TryGetValue((weekIndex, weekday), out var perUser);
                var unassignedMinutes = dayUnassigned.GetValueOrDefault((weekIndex, weekday));
                weekUnassigned += unassignedMinutes;
                var dayUsers = new List<UserDayMinutes>();
                foreach (var user in activeUsers)
                {
                    var minutes = perUser?.GetValueOrDefault(user.Id) ?? 0;
                    var budget = BudgetFor(user, weekday);
                    // A day card compares one day with the daily maximum; the aggregate Monday-Friday / weekend
                    // budget (DailyBudgetMinutes) is checked once per week above.
                    var overBudget = minutes > budget;
                    if (overBudget)
                    {
                        warnings.Add(new PlanIssue(
                            PlanWarningCodes.DailyOverBudget, UserId: user.Id, WeekIndex: weekIndex, Weekday: weekday, Minutes: minutes, Budget: budget));
                    }

                    weekTotals[user.Id] += minutes;
                    dayUsers.Add(new UserDayMinutes(user.Id, minutes, budget, overBudget));
                }

                days.Add(new DaySummary(weekIndex, weekday, dayUsers, unassignedMinutes));
            }

            weeks.Add(new WeekSummary(
                weekIndex,
                activeUsers.Select(u => new UserMinutes(u.Id, weekTotals[u.Id])).ToList(),
                weekUnassigned));
        }

        return new PlanValidation(errors, warnings, new PlanSummary(summaryTasks, days, weeks));
    }

    /// <summary>Later entries win, like building a <c>Map</c> from an array.</summary>
    private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            map[key(item)] = item;
        }

        return map;
    }
}
