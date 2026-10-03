using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Due;

namespace Huishoudplanner.Domain.Limits;

/// <summary>
/// Every limit and default the web app needs, in one place and in groups (the document of <c>GET /api/v2/meta/limits</c>).
/// Port of the constants of <c>packages/shared/src</c>; <c>Vectors/limits.json</c> pins each value to the TypeScript one.
/// A value written as a literal has no exported constant in TypeScript: the comment names the zod schema it is copied from.
/// </summary>
public sealed record HouseholdLimits
{
    public static HouseholdLimits Current { get; } = new();

    public CalendarLimits Calendar { get; init; } = new();

    public TaskLimits Tasks { get; init; } = new();

    public PointsLimits Points { get; init; } = new();

    public BonusLimits Bonuses { get; init; } = new();

    public RewardLimits Rewards { get; init; } = new();

    public BadgeLimits Badges { get; init; } = new();

    public NotificationLimits Notifications { get; init; } = new();

    public AiLimits Ai { get; init; } = new();

    public AuditLimits Audit { get; init; } = new();

    public StatisticsLimits Statistics { get; init; } = new();

    public DefaultValues Defaults { get; init; } = new();
}

/// <summary>Cycle shape and the longest range one calendar request may cover.</summary>
public sealed record CalendarLimits
{
    /// <summary>The longest <c>GET /calendar</c> range in days, both ends included: 53 weeks, like the points entries range (new in v2).</summary>
    public const int MaxRangeDaysValue = 371;

    public int CycleDays { get; init; } = Cycles.CycleDays; // cycle.ts CYCLE_DAYS

    public int CycleWeeks { get; init; } = Cycles.CycleWeeks; // cycle.ts CYCLE_WEEKS

    public int PlanWeeks { get; init; } = 4; // validation/plan.ts PLAN_WEEKS

    public int MaxRangeDays { get; init; } = MaxRangeDaysValue;
}

public sealed record TaskLimits
{
    public int MinPoints { get; init; } = TaskPoints.Min; // points.ts MIN_TASK_POINTS

    public int MaxPoints { get; init; } = TaskPoints.Max; // points.ts MAX_TASK_POINTS

    public int MinDurationMinutes { get; init; } = 1; // schemas/tasks.ts durationMinutes .min(1)

    public int OneOffNameMaxLength { get; init; } = 120; // schemas/occurrences.ts createOneOffOccurrenceInputSchema name .max(120)

    public int IntervalKeyMaxLength { get; init; } = 32; // schemas/intervals.ts key .max(32)

    public int SkipReasonMaxLength { get; init; } = 500; // schemas/occurrences.ts skip reason .max(500)
}

public sealed record PointsLimits
{
    public int MinCentsPerPoint { get; init; } // points.ts MIN_CENTS_PER_POINT = 0

    public int MaxCentsPerPoint { get; init; } = 10000; // points.ts MAX_CENTS_PER_POINT

    public int MaxRedemptionNoteLength { get; init; } = 200; // points.ts MAX_REDEMPTION_NOTE_LENGTH

    public int MaxEntriesRangeDays { get; init; } = 371; // schemas/points.ts MAX_POINTS_ENTRIES_RANGE_DAYS

    public int MaxCorrections { get; init; } = 100; // schemas/points.ts MAX_POINTS_CORRECTIONS
}

public sealed record BonusLimits
{
    public int MinPoints { get; init; } // bonuses.ts MIN_BONUS_POINTS = 0

    public int MaxPoints { get; init; } = 1000; // bonuses.ts MAX_BONUS_POINTS
}

public sealed record RewardLimits
{
    public int MinGoalPoints { get; init; } // rewards.ts MIN_REWARD_GOAL_POINTS = 0

    public int MaxGoalPoints { get; init; } = 100_000; // rewards.ts MAX_REWARD_GOAL_POINTS

    public int EggCount { get; init; } = 10; // rewards.ts REWARD_EGG_COUNT
}

public sealed record BadgeLimits
{
    public int MinNameLength { get; init; } = 1; // badges.ts MIN_BADGE_NAME_LENGTH

    public int MaxNameLength { get; init; } = 60; // badges.ts MAX_BADGE_NAME_LENGTH

    public int MaxDescriptionLength { get; init; } = 200; // badges.ts MAX_BADGE_DESCRIPTION_LENGTH

    public int MaxImageBytes { get; init; } = 256 * 1024; // badges.ts MAX_BADGE_IMAGE_BYTES

    public IReadOnlyList<string> ImageTypes { get; init; } = ["image/png", "image/jpeg", "image/webp"]; // badges.ts BADGE_IMAGE_TYPES

    public int MaxThreshold { get; init; } = 100_000; // badges.ts MAX_BADGE_THRESHOLD

    public int MaxOnTimeWeeksThreshold { get; init; } = 1000; // badges.ts MAX_ON_TIME_WEEKS_THRESHOLD

    public int MaxBadges { get; init; } = 100; // badges.ts MAX_BADGES

    public int MaxRuleTasks { get; init; } = 500; // badges.ts MAX_BADGE_RULE_TASKS
}

public sealed record NotificationLimits
{
    public int MaxBrowserTimes { get; init; } = 6; // schemas/users.ts MAX_BROWSER_NOTIFICATION_TIMES
}

public sealed record AiLimits
{
    public int MinTimeoutSeconds { get; init; } = 10; // schemas/settings.ts MIN_AI_TIMEOUT_SECONDS

    public int MaxTimeoutSeconds { get; init; } = 900; // schemas/settings.ts MAX_AI_TIMEOUT_SECONDS

    public int ConstraintsMaxLength { get; init; } = 2000; // schemas/ai.ts aiConstraintsSchema .max(2000)

    public int PromptMaxLength { get; init; } = 8000; // schemas/settings.ts aiPromptsSchema .max(8000)

    public int PromptTemplateMaxLength { get; init; } = 20000; // schemas/settings.ts aiPromptTemplateSchema .max(20000)
}

public sealed record AuditLimits
{
    public int DefaultPageSize { get; init; } = 50; // schemas/auditLog.ts limit .default(50)

    public int MaxPageSize { get; init; } = 200; // schemas/auditLog.ts limit .max(200)
}

public sealed record StatisticsLimits
{
    public int DefaultCycles { get; init; } = 4; // schemas/stats.ts cycles .default(4)

    public int MaxCycles { get; init; } = 26; // schemas/stats.ts cycles .max(26)
}

/// <summary>Defaults of a fresh installation (the household timezone is configuration, not a default of this document).</summary>
public sealed record DefaultValues
{
    public string CurrencyCode { get; init; } = "EUR"; // points.ts DEFAULT_CURRENCY_CODE

    public int AiTimeoutSeconds { get; init; } = 180; // schemas/settings.ts DEFAULT_AI_TIMEOUT_SECONDS

    public IReadOnlyList<Interval> Intervals { get; init; } = // schemas/intervals.ts DEFAULT_INTERVALS
    [
        new Interval("daily", "Dagelijks", 28, 1),
        new Interval("3w", "3x per week", 12, 2),
        new Interval("2w", "2x per week", 8, 3),
        new Interval("1w", "1x per week", 4, 7),
        new Interval("2wk", "1x per 2 weken", 2, 14),
        new Interval("4wk", "1x per 4 weken", 1, 28),
        new Interval("quarter", "1x per kwartaal", null, 91),
    ];
}
