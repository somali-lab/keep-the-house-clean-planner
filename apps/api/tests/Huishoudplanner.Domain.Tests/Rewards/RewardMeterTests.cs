using Huishoudplanner.Domain.Rewards;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Tests.Rewards;

/// <summary>Scenarios of rewards.test.ts that the golden vectors cannot express (NaN and infinities, 64-bit arithmetic, constants).</summary>
public class RewardMeterTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Eggs_for_a_percentage_that_is_not_a_number_is_zero_like_number_isFinite(double percent) =>
        RewardMeter.EggsForPercent(percent).Should().Be(0);

    [Fact]
    public void The_goal_bounds_and_the_egg_count_are_those_of_the_reward_meter()
    {
        (RewardMeter.MinGoalPoints, RewardMeter.MaxGoalPoints, RewardMeter.EggCount).Should().Be((0, 100_000, 10));
    }

    [Fact]
    public void The_percentage_cannot_overflow_for_the_largest_balances()
    {
        RewardMeter.Percent(long.MaxValue, 1).Should().Be(100);
        RewardMeter.Percent(long.MaxValue / 200, int.MaxValue).Should().Be(100);
        RewardMeter.Percent(int.MaxValue - 1, int.MaxValue).Should().Be(99);
        RewardMeter.Percent(long.MinValue, 10).Should().Be(0);
    }

    [Fact]
    public void The_percentage_only_reaches_100_when_the_goal_is_met()
    {
        for (var goal = 1; goal <= 400; goal++)
        {
            RewardMeter.Percent(goal - 1, goal).Should().BeLessThan(100, "goal {0}", goal);
            RewardMeter.Percent(goal, goal).Should().Be(100, "goal {0}", goal);
        }
    }

    [Fact]
    public void Automatic_goals_equal_each_other_by_value()
    {
        RewardMeter.SameGoals(RewardGoals.Automatic, new RewardGoals(null, null)).Should().BeTrue();
        RewardMeter.SameGoals(RewardGoals.Automatic, new RewardGoals(0, null)).Should().BeFalse();
    }
}
