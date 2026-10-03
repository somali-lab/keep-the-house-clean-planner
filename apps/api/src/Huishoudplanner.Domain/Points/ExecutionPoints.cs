using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Occurrences;

namespace Huishoudplanner.Domain.Points;

/// <summary>
/// What the points ledger needs of an occurrence (the fields <c>expectedExecutionEntry</c> reads). The date is <see langword="null"/> for a
/// stored row whose date cannot be read; old data can hold anything, and one such row must not stop the others.
/// </summary>
public sealed record ExecutionSource(
    string Id,
    string? TaskId,
    DateTimeOffset? Date,
    OccurrenceStatus Status,
    string? CompletedBy,
    string? AssigneeId,
    int? PointsSnapshot,
    int? PointsOverride,
    int DurationMinutesSnapshot,
    string TaskNameSnapshot)
{
    public static ExecutionSource From(Occurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return new ExecutionSource(
            occurrence.Id,
            occurrence.TaskId,
            occurrence.Date,
            occurrence.Status,
            occurrence.CompletedBy,
            occurrence.AssigneeId,
            occurrence.PointsSnapshot,
            occurrence.PointsOverride,
            occurrence.DurationMinutesSnapshot,
            occurrence.TaskNameSnapshot);
    }
}

/// <summary>
/// What an occurrence is expected to leave in the ledger: <see cref="Fields"/> when it earns an entry, <see cref="Unattributed"/> when it earned
/// points but nobody can be credited (counted by the reconciliation, no entry), <see cref="Unreadable"/> when its date cannot be read.
/// </summary>
public sealed record ExecutionExpectation(ExecutionEntryFields? Fields, bool Unattributed = false, bool Unreadable = false)
{
    public static ExecutionExpectation None { get; } = new(Fields: null);
}

/// <summary>The task value a snapshot backfill needs; <see cref="Points"/> is <see langword="null"/> for a task from before points existed.</summary>
public sealed record TaskPointValue(string Id, int? Points, int DurationMinutes);

/// <summary>A points snapshot the reconciliation has to write onto a done occurrence that has none.</summary>
public sealed record SnapshotWrite(string OccurrenceId, int Points);

/// <summary>
/// The pure rules of the execution entries (ADR-0011): which entry an occurrence is expected to leave, and which snapshot a done occurrence
/// from before snapshots gets. Port of <c>expectedExecutionEntry</c> and the field migration of <c>reconcilePoints</c>.
/// </summary>
public static class ExecutionPoints
{
    /// <summary>The unique key of the entry of one occurrence: <c>execution:&lt;occurrenceId&gt;</c>.</summary>
    public static string Key(string occurrenceId)
    {
        ArgumentNullException.ThrowIfNull(occurrenceId);
        return "execution:" + occurrenceId.ToLowerInvariant();
    }

    /// <summary>
    /// The entry an occurrence is expected to produce (ADR-0011). Only a done occurrence earns one, for the person who did the work
    /// (<c>completedBy</c>, else the assignee of older data), when its points are at least 1.
    /// </summary>
    public static ExecutionExpectation Expect(ExecutionSource source, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(zone);
        if (source.Status != OccurrenceStatus.Done)
        {
            return ExecutionExpectation.None;
        }

        var personId = source.CompletedBy ?? source.AssigneeId;
        var amount = source.PointsSnapshot ?? 0;
        if (amount < 1)
        {
            return ExecutionExpectation.None;
        }

        if (personId is null)
        {
            return new ExecutionExpectation(null, Unattributed: true);
        }

        if (source.Date is not { } date)
        {
            return new ExecutionExpectation(null, Unreadable: true);
        }

        var week = DayKeys.FromDayKey(DayKeys.MondayOf(DayKeys.ToDayKey(date, zone)), zone);
        return new ExecutionExpectation(new ExecutionEntryFields(personId, amount, date, week, source.Id, source.TaskId, source.TaskNameSnapshot));
    }

    /// <summary>
    /// The snapshot every done occurrence without one gets: the points a one-off task was recorded with, else the task's points, or the duration
    /// rule for a one-off task, a task that no longer exists, and a task whose points the same run just filled in (it never had a value of its own,
    /// so the duration the occurrence had then counts, not the task's current one). An occurrence that already has a snapshot is never touched, so
    /// a task value changed afterwards never rewrites it.
    /// </summary>
    public static IReadOnlyList<SnapshotWrite> MissingSnapshots(
        IEnumerable<ExecutionSource> done,
        IReadOnlyDictionary<string, TaskPointValue> tasks,
        IReadOnlySet<string> tasksJustDefaulted)
    {
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(tasksJustDefaulted);
        var writes = new List<SnapshotWrite>();
        foreach (var source in done.Where(d => d.PointsSnapshot is null))
        {
            if (source.PointsOverride is { } chosen)
            {
                writes.Add(new SnapshotWrite(source.Id, chosen));
                continue;
            }

            TaskPointValue? task = null;
            var useTask = source.TaskId is { } taskId && tasks.TryGetValue(taskId, out task) && !tasksJustDefaulted.Contains(taskId);
            var points = useTask
                ? task!.Points ?? TaskPoints.DefaultForDuration(task.DurationMinutes)
                : TaskPoints.DefaultForDuration(source.DurationMinutesSnapshot);
            writes.Add(new SnapshotWrite(source.Id, points));
        }

        return writes;
    }
}
