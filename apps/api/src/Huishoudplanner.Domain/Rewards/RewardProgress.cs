using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Rewards;

/// <summary>What <c>GET /points/progress</c> asks of the use case: one person and the period to measure, a week or a cycle.</summary>
public sealed record RewardProgressRequest(string PersonId, PeriodUnit Period);

/// <summary>The money of the progress in whole cents at the factor in force now; <see cref="Goal"/> is <see langword="null"/> when there is no goal.</summary>
public sealed record RewardMoney(long Earned, long? Goal);

/// <summary>
/// The progress of one person towards the goal of the current week or cycle (requirements 4.12). <see cref="Eggs"/> and <see cref="EggCount"/> are
/// delivered by the server so the web app holds no meter rule; <see cref="Money"/> is <see langword="null"/> while a point is worth nothing.
/// </summary>
public sealed record RewardProgress(
    string PersonId,
    PeriodUnit Period,
    DateOnly Start,
    DateOnly End,
    long EarnedPoints,
    int? GoalPoints,
    RewardGoalSource GoalSource,
    int Percent,
    int Eggs,
    int EggCount,
    string CurrencyCode,
    int CentsPerPoint,
    RewardMoney? Money);

/// <summary>
/// A planned occurrence as the reward meter reads it: the bonus fields of the row, the snapshot of its points when it is done and, for work
/// without a snapshot, the task it belongs to (<see langword="null"/> when the task is gone or the occurrence has none).
/// </summary>
public sealed record PlannedWork(BonusSource Occurrence, int? PointsSnapshot, int DurationMinutesSnapshot, TaskPointValue? Task);

/// <summary>The pure rules of the progress read (port of <c>pointsProgress</c> in <c>domain/rewardProgress.ts</c>).</summary>
public static class RewardProgressRules
{
    /// <summary>The week or the cycle of the day <paramref name="today"/> in the household timezone.</summary>
    public static Period PeriodOf(PeriodUnit unit, DateOnly today, DateOnly anchor) =>
        unit == PeriodUnit.Week ? Period.WeekOf(today) : Period.CycleOf(today, anchor);

    /// <summary>
    /// The planned work with its points: the snapshot of work that is done, else what the task is worth now, else the duration rule for
    /// work whose task is gone. A row that cannot be read is left out: old data can hold anything and one row must not hide the meter.
    /// </summary>
    public static IReadOnlyList<GoalOccurrence> GoalOccurrences(IEnumerable<PlannedWork> work, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(zone);
        var items = new List<GoalOccurrence>();
        foreach (var item in work)
        {
            if (item.Occurrence.ToOccurrence(zone) is not { } occurrence)
            {
                continue;
            }

            var points = item.PointsSnapshot
                ?? (item.Task is { } task ? task.Points ?? TaskPoints.DefaultForDuration(task.DurationMinutes) : TaskPoints.DefaultForDuration(item.DurationMinutesSnapshot));
            items.Add(new GoalOccurrence(occurrence, points));
        }

        return items;
    }

    /// <summary>Puts the earned points, the goal and the money together; <paramref name="automatic"/> only counts when no explicit goal is set.</summary>
    public static RewardProgress Build(
        RewardProgressRequest request,
        Period period,
        HouseholdSettings settings,
        long earnedPoints,
        AutomaticGoal automatic)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(automatic);
        var (goalPoints, source) = RewardMeter.ResolveGoal(ExplicitGoal(settings, request.Period), automatic);
        var percent = RewardMeter.Percent(earnedPoints, goalPoints);
        var centsPerPoint = settings.CentsPerPoint ?? 0;
        return new RewardProgress(
            request.PersonId,
            request.Period,
            period.Start,
            period.End,
            earnedPoints,
            goalPoints,
            source,
            percent,
            RewardMeter.EggsForPercent(percent),
            RewardMeter.EggCount,
            settings.CurrencyCode ?? SettingsDefaults.CurrencyCode,
            centsPerPoint,
            centsPerPoint > 0
                ? new RewardMoney(
                    PointsMoney.PointsToCents(earnedPoints, centsPerPoint),
                    goalPoints is { } goal ? PointsMoney.PointsToCents(goal, centsPerPoint) : null)
                : null);
    }

    /// <summary>The goal an administrator set for the period; <see langword="null"/> when the goal is automatic.</summary>
    public static int? ExplicitGoal(HouseholdSettings settings, PeriodUnit unit)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var goals = settings.RewardGoals ?? RewardGoals.Automatic;
        return unit == PeriodUnit.Week ? goals.WeekPoints : goals.CyclePoints;
    }
}
