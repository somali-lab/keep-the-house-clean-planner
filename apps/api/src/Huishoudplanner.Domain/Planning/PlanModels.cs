namespace Huishoudplanner.Domain.Planning;

/// <summary>One placement of a task in the four-week plan. <c>Weekday</c> is 0=Sunday..6=Saturday; no assignee means "anyone".</summary>
public sealed record PlanSlot(string TaskId, int WeekIndex, int Weekday, string? AssigneeId);

/// <summary>The task fields plan validation reads.</summary>
public sealed record PlanTask(string Id, string Name, string IntervalKey, int DurationMinutes, bool Active);

/// <summary>Minutes per day, split into Monday-Friday and the weekend.</summary>
public sealed record DayMinutes(int Weekday, int Weekend);

/// <summary>
/// The person fields plan validation reads. <c>DailyBudgetMinutes</c> is the aggregate Monday-Friday / weekend budget
/// checked once per week; <c>MaxDailyMinutes</c> is the maximum of a single day.
/// </summary>
public sealed record PlanUser(
    string Id,
    string Name,
    bool Active,
    IReadOnlyList<int> UnavailableWeekdays,
    DayMinutes DailyBudgetMinutes,
    DayMinutes MaxDailyMinutes);

/// <summary>A plan as submitted: its slots only; tasks, people and intervals come from the stored data.</summary>
public sealed record PlanDraft(IReadOnlyList<PlanSlot> Slots);

/// <summary>Whether a weekly total is the Monday-Friday or the weekend one.</summary>
public enum BudgetPeriod
{
    Weekday,
    Weekend,
}

/// <summary>Stable codes of the hard rules (errors); each is a message key.</summary>
public static class PlanErrorCodes
{
    public const string WeekIndexOutOfRange = "week_index_out_of_range";
    public const string WeekdayOutOfRange = "weekday_out_of_range";
    public const string UnknownTask = "unknown_task";
    public const string InactiveTask = "inactive_task";
    public const string UnknownUser = "unknown_user";
    public const string InactiveUser = "inactive_user";
    public const string AssigneeUnavailable = "assignee_unavailable";
    public const string DuplicateTaskDay = "duplicate_task_day";
}

/// <summary>Stable codes of the non-blocking warnings.</summary>
public static class PlanWarningCodes
{
    public const string IntervalMismatch = "interval_mismatch";
    public const string OverBudget = "over_budget";
    public const string DailyOverBudget = "daily_over_budget";
}

/// <summary>An error or warning; only the fields that belong to its code are set.</summary>
public sealed record PlanIssue(
    string Code,
    int? SlotIndex = null,
    string? TaskId = null,
    string? UserId = null,
    int? WeekIndex = null,
    int? Weekday = null,
    BudgetPeriod? Period = null,
    int? Placed = null,
    int? Required = null,
    int? Minutes = null,
    int? Budget = null);

/// <summary>Placed slots against the slots the interval asks for; <c>Required</c> is null when the grid does not plan the interval.</summary>
public sealed record TaskSummary(string TaskId, int Placed, int? Required);

public sealed record UserMinutes(string UserId, int Minutes);

public sealed record UserDayMinutes(string UserId, int Minutes, int Budget, bool OverBudget);

/// <summary>One day of the plan; <c>UnassignedMinutes</c> is the work of slots without assignee, counted against no budget.</summary>
public sealed record DaySummary(int WeekIndex, int Weekday, IReadOnlyList<UserDayMinutes> Users, int UnassignedMinutes);

public sealed record WeekSummary(int WeekIndex, IReadOnlyList<UserMinutes> Users, int UnassignedMinutes);

public sealed record PlanSummary(
    IReadOnlyList<TaskSummary> Tasks,
    IReadOnlyList<DaySummary> Days,
    IReadOnlyList<WeekSummary> Weeks);
