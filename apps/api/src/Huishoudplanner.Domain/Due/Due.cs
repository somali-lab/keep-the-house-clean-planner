using Huishoudplanner.Domain.Calendar;

namespace Huishoudplanner.Domain.Due;

/// <summary>State of a task relative to its interval.</summary>
public enum DueState
{
    Ok,
    Due,
    Overdue,
}

/// <summary>A scheduling interval: its key, its label, how often it fits in a cycle and its length in days.</summary>
public sealed record Interval(string Key, string Label, int? PerCycle, int PeriodDays);

/// <summary>The task fields the due calculation reads.</summary>
public sealed record DueTaskInput(
    string Id,
    bool Active,
    string IntervalKey,
    DateTimeOffset? LastCompletedAt,
    DateOnly InitialDueDate);

/// <summary>One entry of the ranked due list. <c>DaysSince</c> starts at one interval on the initial due date.</summary>
public sealed record DueResult(string TaskId, int DaysSince, int PeriodDays, double Ratio, DueState State);

/// <summary>
/// The "hybrid" half of scheduling (port of <c>packages/shared/src/due.ts</c>): a task starts on its initial due
/// date; after its first completion, elapsed time is compared with its interval.
/// </summary>
public static class DueCalculator
{
    public const double DueRatio = 1.0;

    public const double OverdueRatio = 1.5;

    /// <summary>TS <c>DEFAULT_INTERVALS</c>.</summary>
    public static IReadOnlyList<Interval> DefaultIntervals { get; } =
    [
        new("daily", "Dagelijks", 28, 1),
        new("3w", "3x per week", 12, 2),
        new("2w", "2x per week", 8, 3),
        new("1w", "1x per week", 4, 7),
        new("2wk", "1x per 2 weken", 2, 14),
        new("4wk", "1x per 4 weken", 1, 28),
        new("quarter", "1x per kwartaal", null, 91),
    ];

    public static DueState DueStateOf(double ratio) =>
        ratio >= OverdueRatio ? DueState.Overdue : ratio >= DueRatio ? DueState.Due : DueState.Ok;

    /// <summary>
    /// Ranked due list for active tasks, highest ratio first, then most days since, then task id (ordinal).
    /// Days are counted in local calendar days (DST-safe). Before a never-completed task's initial due date its
    /// effective age is zero. Vacation days count too, and a skipped occurrence does not change the last
    /// completion, so skipping keeps a task due. Tasks with an unknown interval key are left out.
    /// </summary>
    public static IReadOnlyList<DueResult> ComputeDue(
        IEnumerable<DueTaskInput> tasks,
        IEnumerable<Interval> intervals,
        DateOnly today,
        TimeZoneInfo timezone)
    {
        var periodByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var interval in intervals)
        {
            periodByKey[interval.Key] = interval.PeriodDays;
        }

        var results = new List<DueResult>();
        foreach (var task in tasks)
        {
            if (!task.Active || !periodByKey.TryGetValue(task.IntervalKey, out var periodDays) || periodDays == 0)
            {
                continue;
            }

            var daysSince = task.LastCompletedAt is { } last
                ? Math.Max(0, DayKeys.DaysBetween(DayKeys.ToDayKey(last, timezone), today))
                : today < task.InitialDueDate
                    ? 0
                    : periodDays + DayKeys.DaysBetween(task.InitialDueDate, today);
            var ratio = (double)daysSince / periodDays;
            results.Add(new DueResult(task.Id, daysSince, periodDays, ratio, DueStateOf(ratio)));
        }

        return results
            .OrderByDescending(r => r.Ratio)
            .ThenByDescending(r => r.DaysSince)
            .ThenBy(r => r.TaskId, StringComparer.Ordinal)
            .ToList();
    }
}
