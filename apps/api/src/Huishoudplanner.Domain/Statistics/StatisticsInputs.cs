using Huishoudplanner.Domain.Occurrences;

namespace Huishoudplanner.Domain.Statistics;

/// <summary>The grouping of the completion report: by task, by room or by the person the work was assigned to.</summary>
public enum StatsGroupBy
{
    Task,
    Room,
    User,
}

/// <summary>The wire names of <see cref="StatsGroupBy"/>.</summary>
public static class StatsGroupByNames
{
    public static string ToWire(StatsGroupBy groupBy) => groupBy switch
    {
        StatsGroupBy.Task => "task",
        StatsGroupBy.Room => "room",
        StatsGroupBy.User => "user",
        _ => throw new ArgumentOutOfRangeException(nameof(groupBy)),
    };

    public static bool TryParse(string? wire, out StatsGroupBy groupBy)
    {
        (var known, groupBy) = wire switch
        {
            "task" => (true, StatsGroupBy.Task),
            "room" => (true, StatsGroupBy.Room),
            "user" => (true, StatsGroupBy.User),
            _ => (false, default(StatsGroupBy)),
        };
        return known;
    }
}

/// <summary>
/// The period of a report: the last <see cref="Cycles"/> cycles up to and including the current one, or, with <see cref="Weeks"/>, the last
/// 1 to 3 calendar weeks including the current one (then <see cref="Cycles"/> is ignored).
/// </summary>
public sealed record StatisticsPeriod(int Cycles, int? Weeks);

/// <summary>
/// The fields of an occurrence that statistics read. Statistics are computed from occurrences and their snapshots, so a later change of a
/// task (duration, name) never rewrites history. <see cref="TaskId"/> is <see langword="null"/> for a one-off task (ADR-0009).
/// </summary>
public sealed record StatisticsOccurrence(
    string CycleId,
    string? TaskId,
    DateTimeOffset Date,
    DateTimeOffset? PlannedDate,
    string? AssigneeId,
    OccurrenceStatus Status,
    DateTimeOffset? CompletedAt,
    string? CompletedBy,
    int DurationMinutesSnapshot,
    string TaskNameSnapshot,
    string? RoomIdSnapshot,
    bool RecordedDone);

/// <summary>A person as the reports need them, in creation order.</summary>
public sealed record StatisticsPerson(string Id, string Name, bool Active);

/// <summary>A task as the reports need it, in name order. <see cref="RoomId"/> is the room it belongs to now.</summary>
public sealed record StatisticsTask(string Id, string Name, string RoomId, string IntervalKey, bool Active);

public sealed record StatisticsRoom(string Id, string Name);
