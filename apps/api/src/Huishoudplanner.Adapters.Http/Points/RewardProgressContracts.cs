using System.ComponentModel;
using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Rewards;

namespace Huishoudplanner.Adapters.Http.Points;

/// <summary>The money of the progress in whole cents at the factor in force now.</summary>
public sealed record RewardMoneyResponse(
    [property: Description("The cents the earned points are worth.")] long Earned,
    [property: Description("The cents the goal is worth; null when there is no goal.")] long? Goal);

/// <summary>How far one person is towards the goal of the current week or cycle.</summary>
public sealed record RewardProgressResponse(
    string PersonId,
    [property: Description("week or cycle.")] string Period,
    [property: Description("The first day of the period, YYYY-MM-DD.")] string Start,
    [property: Description("The last day of the period, YYYY-MM-DD.")] string End,
    [property: Description("The points of executions and bonuses dated in the period; a redemption does not lower them.")] long EarnedPoints,
    [property: Description("The goal in points; null when there is no goal: nothing is planned for the person, or an administrator set 0.")] int? GoalPoints,
    [property: Description("explicit when an administrator set the goal, else automatic: the points of the work planned for the person.")] string GoalSource,
    [property: Description("The share of the goal that is earned, a whole number from 0 to 100; 0 without a goal.")] int Percent,
    [property: Description("The eggs in the basket for the percentage: one per full 10%, 0 to eggCount.")] int Eggs,
    [property: Description("The eggs in the basket when the meter is full.")] int EggCount,
    [property: Description("The ISO 4217 code of the household currency.")] string CurrencyCode,
    [property: Description("The cents one point is worth now; 0 means no money is shown.")] int CentsPerPoint,
    [property: Description("Earned and goal in whole cents; null while a point is worth nothing.")] RewardMoneyResponse? Money)
{
    internal static RewardProgressResponse From(RewardProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return new RewardProgressResponse(
            progress.PersonId,
            progress.Period == PeriodUnit.Week ? "week" : "cycle",
            PointsBalancesResponse.Day(progress.Start),
            PointsBalancesResponse.Day(progress.End),
            progress.EarnedPoints,
            progress.GoalPoints,
            progress.GoalSource == RewardGoalSource.Explicit ? "explicit" : "automatic",
            progress.Percent,
            progress.Eggs,
            progress.EggCount,
            progress.CurrencyCode,
            progress.CentsPerPoint,
            progress.Money is { } money ? new RewardMoneyResponse(money.Earned, money.Goal) : null);
    }
}
