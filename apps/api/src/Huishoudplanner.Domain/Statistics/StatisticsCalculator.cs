using System.Globalization;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Occurrences;

namespace Huishoudplanner.Domain.Statistics;

/// <summary>
/// The four statistics reports as pure calculations (port of <c>domain/stats.ts</c>, whose aggregations ran in MongoDB). Statistics are computed
/// from occurrences and their snapshots, so later changes to a task (duration, name) never rewrite history. A day difference is the difference
/// of the local calendar days in the household timezone, as <c>$dateDiff</c> with <c>unit: day</c> was.
/// </summary>
public static class StatisticsCalculator
{
    /// <summary>Names sort with Dutch collation, as <c>localeCompare(…, 'nl')</c> did.</summary>
    private static readonly CompareInfo Collation = CompareInfo.GetCompareInfo("nl-NL");

    private static readonly Comparer<string> NameOrder = Comparer<string>.Create((a, b) => Collation.Compare(a, b, CompareOptions.None));

    public static WorkloadReport Workload(StatisticsScope scope, IReadOnlyList<StatisticsOccurrence> occurrences, IReadOnlyList<StatisticsPerson> people)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentNullException.ThrowIfNull(people);
        if (scope.Cycles.Count == 0)
        {
            return new WorkloadReport([]);
        }

        var cycleById = scope.Cycles.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var planned = new Dictionary<(string Cycle, int Week, string? User), int>();
        var done = new Dictionary<(string Cycle, int Week, string? User), int>();
        foreach (var o in occurrences.Where(scope.Contains))
        {
            var cycle = cycleById[o.CycleId];
            var week = FloorDiv(DayKeys.DaysBetween(cycle.StartDate, DayKeys.ToDayKey(o.Date, scope.Zone)), 7);
            Add(planned, (o.CycleId, week, o.AssigneeId), o.DurationMinutesSnapshot);
            if (o.Status == OccurrenceStatus.Done)
            {
                Add(done, (o.CycleId, week, o.CompletedBy), o.DurationMinutesSnapshot);
            }
        }

        var appearing = planned.Keys.Concat(done.Keys).Select(k => k.User).Where(u => u is not null).ToHashSet(StringComparer.Ordinal);
        var userIds = people.Where(p => p.Active || appearing.Contains(p.Id)).Select(p => p.Id).ToList();

        static int Sum(Dictionary<(string Cycle, int Week, string? User), int> rows, string cycleId, string? userId, int? week) =>
            rows.Where(r => r.Key.Cycle == cycleId && r.Key.User == userId && (week is null || r.Key.Week == week)).Sum(r => r.Value);

        List<UserWorkload> UsersFor(string cycleId, int? week) =>
            [.. userIds.Select(id => new UserWorkload(id, Sum(planned, cycleId, id, week), Sum(done, cycleId, id, week)))];

        return new WorkloadReport([.. scope.Cycles.Select(cycle => new WorkloadCycle(
            cycle.Index,
            cycle.StartDate,
            cycle.EndDate,
            UsersFor(cycle.Id, null),
            Sum(planned, cycle.Id, null, null),
            [.. Enumerable.Range(0, Calendar.Cycles.CycleWeeks)
                .Where(week =>
                {
                    var start = DayKeys.AddDays(cycle.StartDate, week * 7);
                    return (scope.FromKey is null || start >= scope.FromKey) && (scope.ToKey is null || start < scope.ToKey);
                })
                .Select(week => new WorkloadWeek(week, DayKeys.AddDays(cycle.StartDate, week * 7), UsersFor(cycle.Id, week), Sum(planned, cycle.Id, null, week)))]))]);
    }

    /// <param name="scope">The period.</param>
    /// <param name="occurrences">The occurrences of the period (more are fine; those outside the scope are left out).</param>
    /// <param name="groupBy">The grouping of the rows.</param>
    /// <param name="startOfToday">Midnight of today in the household timezone: an open occurrence before it counts as missed.</param>
    /// <param name="tasks">The tasks, for the names and for the room a task belongs to now.</param>
    /// <param name="rooms">The rooms, for the names.</param>
    /// <param name="people">The people, for the names.</param>
    public static CompletionReport Completion(
        StatisticsScope scope,
        IReadOnlyList<StatisticsOccurrence> occurrences,
        StatsGroupBy groupBy,
        DateTimeOffset startOfToday,
        IReadOnlyList<StatisticsTask> tasks,
        IReadOnlyList<StatisticsRoom> rooms,
        IReadOnlyList<StatisticsPerson> people)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(people);
        var wire = StatsGroupByNames.ToWire(groupBy);
        if (scope.Cycles.Count == 0)
        {
            return new CompletionReport(wire, []);
        }

        var taskById = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var counted = occurrences.Where(scope.Contains).Where(o =>
            o.Status is OccurrenceStatus.Done or OccurrenceStatus.Skipped || (o.Status == OccurrenceStatus.Open && o.Date < startOfToday));

        string? KeyOf(StatisticsOccurrence o) => groupBy switch
        {
            StatsGroupBy.Task => o.TaskId,
            StatsGroupBy.User => o.AssigneeId,
            // A one-off task has no task record: its room is the snapshot (ADR-0009).
            _ => o.TaskId is { } id && taskById.TryGetValue(id, out var task) ? task.RoomId : o.RoomIdSnapshot,
        };

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, name) in tasks.Select(t => (t.Id, t.Name)).Concat(rooms.Select(r => (r.Id, r.Name))).Concat(people.Select(p => (p.Id, p.Name))))
        {
            names[id] = name;
        }

        var rows = counted
            .GroupBy(KeyOf)
            .Select(g =>
            {
                var done = g.Count(o => o.Status == OccurrenceStatus.Done);
                var skipped = g.Count(o => o.Status == OccurrenceStatus.Skipped);
                var missed = g.Count(o => o.Status == OccurrenceStatus.Open);
                var total = done + skipped + missed;
                var snapshotName = g.Select(o => o.TaskNameSnapshot).Max(StringComparer.Ordinal) ?? string.Empty;
                return new CompletionRow(
                    g.Key,
                    g.Key is null ? string.Empty : names.GetValueOrDefault(g.Key, snapshotName),
                    done,
                    skipped,
                    missed,
                    total == 0 ? null : done / (double)total);
            })
            .OrderBy(r => r.Rate ?? 2)
            .ThenBy(r => r.Name, NameOrder)
            .ToList();
        return new CompletionReport(wire, rows);
    }

    public static IntervalReport Intervals(
        StatisticsScope scope,
        IReadOnlyList<StatisticsOccurrence> occurrences,
        IReadOnlyList<StatisticsTask> tasks,
        IReadOnlyList<Interval> intervals)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(intervals);
        var byTask = new Dictionary<string, (int Completions, double? AverageDays)>(StringComparer.Ordinal);
        if (scope.Cycles.Count > 0)
        {
            // One-off tasks (no task id) have no interval (ADR-0009).
            var completed = occurrences.Where(scope.Contains).Where(o => o.TaskId is not null && o.Status == OccurrenceStatus.Done && o.CompletedAt is not null);
            foreach (var group in completed.GroupBy(o => o.TaskId!))
            {
                var days = group.OrderBy(o => o.CompletedAt).Select(o => DayKeys.ToDayKey(o.CompletedAt!.Value, scope.Zone)).ToList();
                var gaps = days.Zip(days.Skip(1), DayKeys.DaysBetween).ToList();
                byTask[group.Key] = (days.Count, gaps.Count == 0 ? null : gaps.Average());
            }
        }

        var periodByKey = intervals.ToDictionary(i => i.Key, i => i.PeriodDays, StringComparer.Ordinal);
        var rows = tasks
            .Where(t => t.Active || byTask.ContainsKey(t.Id))
            .Select(t =>
            {
                byTask.TryGetValue(t.Id, out var stats);
                var periodDays = periodByKey.GetValueOrDefault(t.IntervalKey, 0);
                var averageDays = stats.AverageDays;
                return new IntervalRow(t.Id, t.Name, t.IntervalKey, periodDays, stats.Completions, averageDays, averageDays is null || periodDays == 0 ? null : averageDays / periodDays);
            })
            .OrderByDescending(r => r.Deviation ?? double.NegativeInfinity)
            .ThenBy(r => r.Name, NameOrder)
            .ToList();
        return new IntervalReport(rows);
    }

    public static DeviationReport Deviations(StatisticsScope scope, IReadOnlyList<StatisticsOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(occurrences);
        if (scope.Cycles.Count == 0)
        {
            return new DeviationReport([]);
        }

        // Recorded work and one-off tasks have no planned slot to deviate from (ADR-0009).
        var completed = occurrences.Where(scope.Contains).Where(o =>
            o.Status == OccurrenceStatus.Done && o.CompletedAt is not null && o.TaskId is not null && !o.RecordedDone);
        var rows = completed
            .GroupBy(o => o.TaskId!)
            .Select(g =>
            {
                var measured = g.Select(o =>
                {
                    var date = DayKeys.ToDayKey(o.Date, scope.Zone);
                    var planned = o.PlannedDate is { } p ? DayKeys.ToDayKey(p, scope.Zone) : date;
                    return (Shift: DayKeys.DaysBetween(planned, date), Delay: DayKeys.DaysBetween(date, DayKeys.ToDayKey(o.CompletedAt!.Value, scope.Zone)));
                }).ToList();
                return new DeviationRow(
                    g.Key,
                    g.Select(o => o.TaskNameSnapshot).Max(StringComparer.Ordinal) ?? string.Empty,
                    measured.Count,
                    measured.Average(m => m.Shift),
                    measured.Average(m => m.Delay),
                    measured.Count(m => m.Delay < 0),
                    measured.Count(m => m.Delay == 0),
                    measured.Count(m => m.Delay > 0));
            })
            .OrderByDescending(r => Math.Abs(r.AverageCompletionDelayDays))
            .ThenByDescending(r => Math.Abs(r.AveragePlanningShiftDays))
            .ThenBy(r => r.Name, NameOrder)
            .ToList();
        return new DeviationReport(rows);
    }

    private static void Add(Dictionary<(string Cycle, int Week, string? User), int> rows, (string Cycle, int Week, string? User) key, int minutes) =>
        rows[key] = rows.GetValueOrDefault(key) + minutes;

    private static int FloorDiv(int n, int m) => (int)Math.Floor(n / (double)m);
}
