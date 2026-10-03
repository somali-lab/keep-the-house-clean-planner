namespace Huishoudplanner.Domain.Statistics;

/// <summary>Snapshot minutes of one person in a period.</summary>
/// <param name="UserId">The person.</param>
/// <param name="PlannedMinutes">Snapshot minutes of the occurrences assigned to this person.</param>
/// <param name="DoneMinutes">Snapshot minutes of the occurrences this person completed (<c>completedBy</c>).</param>
public sealed record UserWorkload(string UserId, int PlannedMinutes, int DoneMinutes);

/// <summary>One week of a cycle in the workload report. <see cref="UnassignedPlannedMinutes"/> is the work of "anyone".</summary>
public sealed record WorkloadWeek(int WeekIndex, DateOnly StartDate, IReadOnlyList<UserWorkload> Users, int UnassignedPlannedMinutes);

public sealed record WorkloadCycle(
    int Index,
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyList<UserWorkload> Users,
    int UnassignedPlannedMinutes,
    IReadOnlyList<WorkloadWeek> Weeks);

/// <summary>Planned and done minutes per person for every cycle of the period, oldest first, so the list reads as a trend.</summary>
public sealed record WorkloadReport(IReadOnlyList<WorkloadCycle> Cycles);

/// <param name="Key">The task, room or person; <see langword="null"/> for the combined row of one-off tasks, of work without a room or of work without an assignee.</param>
/// <param name="Missed">Still open on a day before today.</param>
/// <param name="Rate">done / (done + skipped + missed); <see langword="null"/> when nothing was due yet.</param>
public sealed record CompletionRow(string? Key, string Name, int Done, int Skipped, int Missed, double? Rate);

public sealed record CompletionReport(string GroupBy, IReadOnlyList<CompletionRow> Rows);

/// <param name="AverageDays">Average local calendar days between consecutive completions; <see langword="null"/> with fewer than two.</param>
/// <param name="Deviation">AverageDays / PeriodDays; above 1 means it happens less often than intended.</param>
public sealed record IntervalRow(string TaskId, string Name, string IntervalKey, int PeriodDays, int Completions, double? AverageDays, double? Deviation);

public sealed record IntervalReport(IReadOnlyList<IntervalRow> Rows);

/// <param name="AveragePlanningShiftDays">Average number of calendar days between the original and the current planned date.</param>
/// <param name="AverageCompletionDelayDays">Average number of calendar days between the current planned date and the completion.</param>
public sealed record DeviationRow(
    string TaskId,
    string Name,
    int Completions,
    double AveragePlanningShiftDays,
    double AverageCompletionDelayDays,
    int Early,
    int OnTime,
    int Late);

public sealed record DeviationReport(IReadOnlyList<DeviationRow> Rows);
