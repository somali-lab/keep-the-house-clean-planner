using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Rewards;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Tests.Rewards;

/// <summary>The pure rules of <c>GET /points/progress</c>: the period, the points of the planned work and the values the reward meter delivers.</summary>
public class RewardProgressRulesTests
{
    private const string P1 = "aaaaaaaaaaaaaaaaaaaaaaa1";
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");
    private static readonly DateOnly Anchor = new(2026, 9, 14);
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    private static DateOnly D(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static HouseholdSettings Settings(int? week = null, int? cycle = null, int? cents = null, string? currency = null) =>
        SettingsDefaults.ForNewInstallation("Europe/Amsterdam", Anchor, Now) with
        {
            RewardGoals = week is null && cycle is null ? null : new RewardGoals(week, cycle),
            CentsPerPoint = cents,
            CurrencyCode = currency,
        };

    private static PlannedWork Work(string day, int? snapshot = null, int duration = 7, TaskPointValue? task = null, OccurrenceStatus? status = OccurrenceStatus.Open)
    {
        var instant = DayKeys.FromDayKey(D(day), Amsterdam);
        return new PlannedWork(new BonusSource("bbbbbbbbbbbbbbbbbbbbbbb1", status, instant, instant, false, P1, false, null, null, null), snapshot, duration, task);
    }

    private static int PointsOf(PlannedWork work) => RewardProgressRules.GoalOccurrences([work], Amsterdam).Single().Points;

    // ---- the period

    [Theory]
    [InlineData("2026-09-16", "2026-09-14", "2026-09-20")]
    [InlineData("2026-09-20", "2026-09-14", "2026-09-20")]
    [InlineData("2026-09-21", "2026-09-21", "2026-09-27")]
    public void PeriodOf_aWeekRunsFromMondayToSunday(string today, string start, string end)
    {
        var period = RewardProgressRules.PeriodOf(PeriodUnit.Week, D(today), Anchor);

        (period.Start, period.End).Should().Be((D(start), D(end)));
    }

    [Fact]
    public void PeriodOf_aCycleIsTheFourWeeksOfTheAnchor() =>
        RewardProgressRules.PeriodOf(PeriodUnit.Cycle, D("2026-10-22"), Anchor).Should().Match<Period>(p => p.Start == D("2026-10-12") && p.End == D("2026-11-08"));

    // ---- the points of the planned work

    [Fact]
    public void GoalOccurrences_theSnapshotWinsOverTheTask() =>
        PointsOf(Work("2026-09-14", snapshot: 9, task: new TaskPointValue("t", 4, 30))).Should().Be(9);

    [Fact]
    public void GoalOccurrences_withoutASnapshotTheTaskIsWorthItsPointsNow() =>
        PointsOf(Work("2026-09-14", task: new TaskPointValue("t", 4, 30))).Should().Be(4);

    [Fact]
    public void GoalOccurrences_aTaskFromBeforePointsIsWorthItsDurationRule() =>
        PointsOf(Work("2026-09-14", task: new TaskPointValue("t", null, 30))).Should().Be(30);

    [Theory]
    [InlineData(7, 7)]
    [InlineData(0, 1)]
    [InlineData(5000, 1000)]
    public void GoalOccurrences_workWhoseTaskIsGoneIsWorthTheDurationOfItsSnapshot(int duration, int expected) =>
        PointsOf(Work("2026-09-14", duration: duration)).Should().Be(expected);

    [Fact]
    public void GoalOccurrences_aRowThatCannotBeReadIsLeftOut() =>
        RewardProgressRules.GoalOccurrences([Work("2026-09-14", status: null), Work("2026-09-15", snapshot: 2)], Amsterdam).Should().ContainSingle().Which.Points.Should().Be(2);

    // ---- the values of the meter

    [Fact]
    public void Build_deliversThePercentTheEggsAndTheMoney()
    {
        var period = Period.WeekOf(D("2026-09-16"));

        var progress = RewardProgressRules.Build(new RewardProgressRequest(P1, PeriodUnit.Week), period, Settings(cents: 25, currency: "USD"), 3, new AutomaticGoal(2, 4));

        progress.Should().BeEquivalentTo(new RewardProgress(
            P1, PeriodUnit.Week, D("2026-09-14"), D("2026-09-20"), 3, 4, RewardGoalSource.Automatic, 75, 7, 10, "USD", 25, new RewardMoney(75, 100)));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(9, 90, 9)]
    [InlineData(10, 100, 10)]
    [InlineData(25, 100, 10)]
    public void Build_theEggsFollowThePercentAndAreCappedAtTen(int earned, int percent, int eggs)
    {
        var progress = RewardProgressRules.Build(new RewardProgressRequest(P1, PeriodUnit.Week), Period.WeekOf(D("2026-09-16")), Settings(week: 10), earned, new AutomaticGoal(0, 0));

        (progress.Percent, progress.Eggs, progress.EggCount).Should().Be((percent, eggs, 10));
        progress.EarnedPoints.Should().Be(earned);
    }

    [Fact]
    public void Build_anExplicitZeroSwitchesTheMeterOffForThatPeriodOnly()
    {
        var settings = Settings(week: 0, cycle: 2);

        var week = RewardProgressRules.Build(new RewardProgressRequest(P1, PeriodUnit.Week), Period.WeekOf(D("2026-09-16")), settings, 3, new AutomaticGoal(1, 8));
        var cycle = RewardProgressRules.Build(new RewardProgressRequest(P1, PeriodUnit.Cycle), Period.CycleOf(D("2026-09-16"), Anchor), settings, 3, new AutomaticGoal(1, 8));

        (week.GoalPoints, week.GoalSource, week.Percent, week.Eggs).Should().Be((null, RewardGoalSource.Explicit, 0, 0));
        (cycle.GoalPoints, cycle.GoalSource, cycle.Percent).Should().Be((2, RewardGoalSource.Explicit, 100));
    }

    [Fact]
    public void Build_withoutAGoalTheGoalMoneyIsNullAndWithoutAFactorThereIsNoMoney()
    {
        var request = new RewardProgressRequest(P1, PeriodUnit.Week);
        var period = Period.WeekOf(D("2026-09-16"));

        RewardProgressRules.Build(request, period, Settings(cents: 25), 0, new AutomaticGoal(0, 0)).Money.Should().Be(new RewardMoney(0, null));
        RewardProgressRules.Build(request, period, Settings(), 3, new AutomaticGoal(1, 4)).Should().Match<RewardProgress>(p => p.Money == null && p.CentsPerPoint == 0 && p.CurrencyCode == "EUR");
    }

    [Fact]
    public void Build_aBalanceThatDoesNotFitInThirtyTwoBitsStaysExact()
    {
        var progress = RewardProgressRules.Build(new RewardProgressRequest(P1, PeriodUnit.Week), Period.WeekOf(D("2026-09-16")), Settings(week: 5, cents: 100), 3_000_000_000, new AutomaticGoal(0, 0));

        (progress.EarnedPoints, progress.Percent, progress.Money).Should().Be((3_000_000_000, 100, new RewardMoney(300_000_000_000, 500)));
    }
}
