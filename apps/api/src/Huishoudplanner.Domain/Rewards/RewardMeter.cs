using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Rewards;

/// <summary>A planned occurrence with the points it is worth: its snapshot when it is done, else what the task is worth now.</summary>
public sealed record GoalOccurrence(BonusOccurrence Occurrence, int Points);

/// <summary>The automatic goal of a person in a period.</summary>
/// <param name="Planned">Planned occurrences of the person as owner in the period; skipped work and recorded work do not count.</param>
/// <param name="Points">The sum of their points.</param>
public sealed record AutomaticGoal(int Planned, int Points);

public enum RewardGoalSource
{
    Explicit,
    Automatic,
}

/// <summary>The goal of a period; <see cref="GoalPoints"/> is <see langword="null"/> when there is no goal: nothing planned for the person, or an explicit 0.</summary>
public sealed record ResolvedRewardGoal(int? GoalPoints, RewardGoalSource Source);

/// <summary>
/// The reward meter (requirements 4.12; port of <c>packages/shared/src/rewards.ts</c>): pure rules over day keys and string
/// ids. A period is the calendar week or the cycle of <see cref="Period"/>; the earned points come from the ledger and the
/// goal is either set by an administrator (<see cref="RewardGoals"/>) or the points of the work planned for the person.
/// </summary>
public static class RewardMeter
{
    /// <summary>Smallest and largest explicit goal of a week or a cycle, in points. 0 means no goal.</summary>
    public const int MinGoalPoints = 0;

    public const int MaxGoalPoints = 100_000;

    /// <summary>Eggs in the basket when the meter is full: one egg per 10%.</summary>
    public const int EggCount = 10;

    /// <summary>TS <c>sameRewardGoals</c>.</summary>
    public static bool SameGoals(RewardGoals a, RewardGoals b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.WeekPoints == b.WeekPoints && a.CyclePoints == b.CyclePoints;
    }

    /// <summary>
    /// The automatic goal of a person (TS <c>automaticGoal</c>): the points of the work planned for them in the period. The
    /// owner is the one of the bonuses (<see cref="BonusOccurrence.PeriodOwnerId"/>) and the day that places an occurrence
    /// in a period is its planned day, so overdue work dragged to today still belongs to the week it was planned in and work
    /// somebody else did stays in the owner's goal. Recorded extra work was never planned and is left out; skipped work cannot
    /// be earned and is left out too.
    /// </summary>
    public static AutomaticGoal AutomaticGoalFor(IReadOnlyList<GoalOccurrence> items, string personId, Period period)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(period);
        var planned = 0;
        var points = 0;
        foreach (var item in items)
        {
            var occurrence = item.Occurrence;
            if (occurrence.RecordedDone || occurrence.Status == OccurrenceStatus.Skipped)
            {
                continue;
            }

            if (!string.Equals(occurrence.PeriodOwnerId, personId, StringComparison.Ordinal))
            {
                continue;
            }

            var day = occurrence.PeriodDay;
            if (day < period.Start || day > period.End)
            {
                continue;
            }

            planned++;
            points += item.Points;
        }

        return new AutomaticGoal(planned, points);
    }

    /// <summary>
    /// The goal of a period (TS <c>resolveRewardGoal</c>): the explicit goal when an administrator set one (0 switches the
    /// goal off), else the automatic goal, at least 1, and none at all when nothing is planned for the person.
    /// </summary>
    public static ResolvedRewardGoal ResolveGoal(int? explicitGoal, AutomaticGoal automatic)
    {
        ArgumentNullException.ThrowIfNull(automatic);
        if (explicitGoal is { } goal)
        {
            return new ResolvedRewardGoal(goal > 0 ? goal : null, RewardGoalSource.Explicit);
        }

        return new ResolvedRewardGoal(automatic.Planned > 0 ? Math.Max(1, automatic.Points) : null, RewardGoalSource.Automatic);
    }

    /// <summary>
    /// The share of the goal that is earned, a whole number from 0 to 100 (TS <c>rewardPercent</c>); it only reaches 100 when the
    /// goal is met. The multiplication is done in 64 bits, so large balances cannot overflow.
    /// </summary>
    public static int Percent(long earnedPoints, int? goalPoints)
    {
        if (goalPoints is not { } goal || goal <= 0 || earnedPoints <= 0)
        {
            return 0;
        }

        // earned * 100 / goal with both positive: integer division is the floor of the quotient. Cap before multiplying
        // when earned is so large that the product would not fit in a long.
        if (earnedPoints >= goal)
        {
            return 100;
        }

        return (int)Math.Min(100, earnedPoints * 100 / goal);
    }

    /// <summary>
    /// Eggs in the basket for a percentage: one per full 10%, 0 to <see cref="EggCount"/> (TS <c>eggsForPercent</c>). Like the
    /// TypeScript <c>Number.isFinite</c> test, NaN and both infinities give 0.
    /// </summary>
    public static int EggsForPercent(double percent)
    {
        if (!double.IsFinite(percent))
        {
            return 0;
        }

        return (int)Math.Min(EggCount, Math.Max(0, Math.Floor(percent / 10)));
    }
}
