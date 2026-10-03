using System.Globalization;
using Huishoudplanner.Application.Points;
using Huishoudplanner.Application.Tests.Settings;
using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Rewards;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Application.Tests.Points;

/// <summary>
/// <c>points-progress.test.ts</c> at the level of the use case, with fakes for the ledger, the settings and the planned work. Monday 14 Sep 2026 is the
/// first day of cycle 0 (14 Sep to 11 Oct) and the clock stands on Wednesday 16 Sep. The ownership rules (take-over, a finished week keeps its owner)
/// belong to the occurrence use cases and are covered end to end by the endpoint tests.
/// </summary>
public sealed class RewardProgressServiceTests
{
    private const string P1 = "000000000000000000000001";
    private const string P2 = "000000000000000000000002";
    private const string Guest = "000000000000000000000003";
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly D(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset Midnight(string day) => DayKeys.FromDayKey(D(day), Amsterdam);

    private sealed class FakePlannedWork : ForReadingPlannedWork
    {
        public List<PlannedWork> Items { get; } = [];

        public PortError? Failure { get; set; }

        public int Reads { get; private set; }

        public Task<OneOf<IReadOnlyList<PlannedWork>, PortError>> FindPlannedAsync(DateTimeOffset from, DateTimeOffset toExclusive, CancellationToken cancellationToken)
        {
            Reads++;
            if (Failure is { } failure)
            {
                return Task.FromResult<OneOf<IReadOnlyList<PlannedWork>, PortError>>(failure);
            }

            // The store filters on the planned day, like the real read; recorded work is never returned.
            return Task.FromResult(OneOf<IReadOnlyList<PlannedWork>, PortError>.FromT0(
                [.. Items.Where(w => w.Occurrence.PlannedDate >= from && w.Occurrence.PlannedDate < toExclusive && !w.Occurrence.RecordedDone)]));
        }
    }

    private sealed class World
    {
        public FakeSettingsStore Settings { get; } = new(SettingsSamples.Seeded());

        public FakePointEntryStore Ledger { get; } = new();

        public FakePlannedWork Planned { get; } = new();

        public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));

        public RewardProgressService Service => new(Settings, Ledger, Planned, Clock);

        /// <summary>The household of the Node test: Stofzuigen (3) on Monday for person 1, Tuesday for person 2 and Wednesday for nobody; Dweilen (1) on Thursday of week 0 and Monday of week 1, both for person 1.</summary>
        public World WithTheNodeHousehold()
        {
            Plan("2026-09-14", P1, 3);
            Plan("2026-09-15", P2, 3);
            Plan("2026-09-16", null, 3);
            Plan("2026-09-17", P1, 1);
            Plan("2026-09-21", P1, 1);
            return this;
        }

        public PlannedWork Plan(string day, string? person, int points, OccurrenceStatus status = OccurrenceStatus.Open, bool recorded = false)
        {
            var work = new PlannedWork(
                new BonusSource(Ledger.NextId(), status, Midnight(day), Midnight(day), recorded, person, false, null, null, null), null, points, new TaskPointValue("t", points, points));
            Planned.Items.Add(work);
            return work;
        }

        public void Earn(string person, string day, int amount, PointEntryKind kind = PointEntryKind.Execution)
        {
            var date = Midnight(day);
            var id = Ledger.NextId();
            var occurrence = Ledger.NextId();
            Ledger.Items.Add(new PointEntry(
                id,
                kind == PointEntryKind.Execution ? "execution:" + occurrence : kind + ":" + id,
                kind,
                person,
                amount,
                date,
                date,
                null,
                kind == PointEntryKind.Execution ? occurrence : null,
                null,
                "Taak",
                PointEntrySource.Live,
                null,
                null,
                null,
                date,
                date));
        }

        public async Task<RewardProgress> ProgressAsync(string person, PeriodUnit period)
        {
            var result = await Service.ProgressAsync(new RewardProgressRequest(person, period), Ct);
            return result.IsT0 ? result.AsT0 : throw new InvalidOperationException(result.ToString());
        }

        public void SetGoals(int? week, int? cycle) =>
            Settings.Document = Settings.Document! with { RewardGoals = new RewardGoals(week, cycle) };

        public void ChangeWork(int index, Func<BonusSource, BonusSource> change) =>
            Planned.Items[index] = Planned.Items[index] with { Occurrence = change(Planned.Items[index].Occurrence) };
    }

    [Fact]
    public async Task UsesThePointsPlannedForThePersonAsTheGoalPerWeekAndPerCycle_andCountsWhatTheyEarned()
    {
        var w = new World().WithTheNodeHousehold();

        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().BeEquivalentTo(new RewardProgress(
            P1, PeriodUnit.Week, D("2026-09-14"), D("2026-09-20"), 0, 4, RewardGoalSource.Automatic, 0, 0, 10, "EUR", 0, null));
        (await w.ProgressAsync(P1, PeriodUnit.Cycle)).Should().Match<RewardProgress>(p => p.Start == D("2026-09-14") && p.End == D("2026-10-11") && p.GoalPoints == 5 && p.EarnedPoints == 0);
        (await w.ProgressAsync(P2, PeriodUnit.Week)).GoalPoints.Should().Be(3);
        (await w.ProgressAsync(P2, PeriodUnit.Cycle)).GoalPoints.Should().Be(3);

        w.Earn(P1, "2026-09-14", 3);
        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.EarnedPoints == 3 && p.GoalPoints == 4 && p.Percent == 75 && p.Eggs == 7);
        (await w.ProgressAsync(P1, PeriodUnit.Cycle)).Should().Match<RewardProgress>(p => p.EarnedPoints == 3 && p.GoalPoints == 5 && p.Percent == 60 && p.Eggs == 6);
        (await w.ProgressAsync(P2, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.EarnedPoints == 0 && p.Percent == 0);

        w.Earn(P1, "2026-09-17", 1);
        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.EarnedPoints == 4 && p.GoalPoints == 4 && p.Percent == 100 && p.Eggs == 10);
    }

    [Fact]
    public async Task SaysThereIsNoGoalWhenNothingIsPlannedForThePerson_andLeavesOutSkippedWork()
    {
        var w = new World().WithTheNodeHousehold();

        (await w.ProgressAsync(Guest, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.EarnedPoints == 0 && p.GoalPoints == null && p.GoalSource == RewardGoalSource.Automatic && p.Percent == 0 && p.Money == null);
        (await w.ProgressAsync(Guest, PeriodUnit.Cycle)).Should().Match<RewardProgress>(p => p.GoalPoints == null && p.Percent == 0);

        // Skipped work cannot be earned, so it leaves the goal: Dweilen of Thursday goes, Stofzuigen stays.
        w.ChangeWork(3, o => o with { Status = OccurrenceStatus.Skipped });
        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.GoalPoints == 3 && p.GoalSource == RewardGoalSource.Automatic);
    }

    [Fact]
    public async Task LeavesRecordedWorkOutOfTheGoal_butCountsItsPointsAsEarned()
    {
        var w = new World().WithTheNodeHousehold();
        w.Plan("2026-09-15", P1, 5, OccurrenceStatus.Done, recorded: true);
        w.Earn(P1, "2026-09-15", 5);

        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.GoalPoints == 4 && p.EarnedPoints == 5 && p.Percent == 100);
    }

    [Fact]
    public async Task KeepsWorkThatSomebodyElseDidInTheGoalOfItsOwner_andCreditsThePersonWhoDidIt()
    {
        var w = new World().WithTheNodeHousehold();
        w.ChangeWork(1, o => o with { Status = OccurrenceStatus.Done, CompletedBy = P1 });
        w.Earn(P1, "2026-09-15", 3);

        (await w.ProgressAsync(P2, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.GoalPoints == 3 && p.EarnedPoints == 0 && p.Percent == 0);
        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.GoalPoints == 4 && p.EarnedPoints == 3 && p.Percent == 75);
    }

    [Fact]
    public async Task FollowsTheFrozenOwnerOfFinishedWork()
    {
        var w = new World().WithTheNodeHousehold();
        // Person 1 took over the work of person 2 after its week had ended: it stays with person 2.
        w.ChangeWork(1, o => o with { AssigneeId = P1, HasFrozenOwner = true, FrozenOwnerId = P2 });

        (await w.ProgressAsync(P2, PeriodUnit.Cycle)).GoalPoints.Should().Be(3);
        (await w.ProgressAsync(P1, PeriodUnit.Cycle)).GoalPoints.Should().Be(5);
    }

    [Fact]
    public async Task TakesTheExplicitGoalOver_switchesItOffWithZero_andKeepsTheOtherPeriodAutomatic()
    {
        var w = new World().WithTheNodeHousehold();
        w.Earn(P1, "2026-09-14", 3);
        w.SetGoals(6, null);

        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.GoalPoints == 6 && p.GoalSource == RewardGoalSource.Explicit && p.EarnedPoints == 3 && p.Percent == 50);
        (await w.ProgressAsync(P1, PeriodUnit.Cycle)).Should().Match<RewardProgress>(p => p.GoalPoints == 5 && p.GoalSource == RewardGoalSource.Automatic && p.Percent == 60);
        // An explicit goal also applies to a person nothing is planned for.
        (await w.ProgressAsync(Guest, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.GoalPoints == 6 && p.GoalSource == RewardGoalSource.Explicit && p.Percent == 0);

        w.SetGoals(0, 2);
        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.GoalPoints == null && p.GoalSource == RewardGoalSource.Explicit && p.Percent == 0 && p.EarnedPoints == 3);
        // More earned than the goal is capped at 100%, while the points stay exact.
        (await w.ProgressAsync(P1, PeriodUnit.Cycle)).Should().Match<RewardProgress>(p => p.GoalPoints == 2 && p.Percent == 100 && p.EarnedPoints == 3 && p.Eggs == 10);
    }

    [Fact]
    public async Task ReadsThePlannedWorkOnlyWhenTheGoalIsAutomatic()
    {
        var w = new World().WithTheNodeHousehold();
        w.SetGoals(6, 10);

        await w.ProgressAsync(P1, PeriodUnit.Week);
        await w.ProgressAsync(P1, PeriodUnit.Cycle);

        w.Planned.Reads.Should().Be(0);
    }

    [Fact]
    public async Task ShowsMoneyWhenAPointIsWorthSomething_whatIsEarnedAndWhatTheGoalIsWorth()
    {
        var w = new World().WithTheNodeHousehold();
        w.Settings.Document = w.Settings.Document! with { CurrencyCode = "USD", CentsPerPoint = 25 };
        w.Earn(P1, "2026-09-14", 3);

        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p =>
            p.EarnedPoints == 3 && p.GoalPoints == 4 && p.CurrencyCode == "USD" && p.CentsPerPoint == 25 && p.Money == new RewardMoney(75, 100));
        (await w.ProgressAsync(Guest, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.GoalPoints == null && p.Money == new RewardMoney(0, null));
    }

    [Fact]
    public async Task CountsTheBonusesOfEndedWeeksInTheCycle_andCapsThePercentage()
    {
        var w = new World().WithTheNodeHousehold();
        w.Earn(P1, "2026-09-14", 3);
        w.Earn(P1, "2026-09-17", 1);
        w.Earn(P1, "2026-09-20", 5, PointEntryKind.BonusWeekDone);
        w.Clock.Now = new DateTimeOffset(2026, 9, 21, 1, 0, 0, TimeSpan.Zero);

        // The new week has earned nothing yet; the bonus belongs to the week that ended and to the cycle: 3 + 1 + 5 against a goal of 5.
        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.Start == D("2026-09-21") && p.End == D("2026-09-27") && p.EarnedPoints == 0 && p.GoalPoints == 1 && p.Percent == 0);
        (await w.ProgressAsync(P1, PeriodUnit.Cycle)).Should().Match<RewardProgress>(p => p.EarnedPoints == 9 && p.GoalPoints == 5 && p.Percent == 100);
    }

    [Fact]
    public async Task DoesNotLetARedemptionLowerTheProgress()
    {
        var w = new World().WithTheNodeHousehold();
        w.Earn(P1, "2026-09-14", 3);
        w.Earn(P1, "2026-09-15", -2, PointEntryKind.Redemption);

        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.EarnedPoints == 3 && p.Percent == 75);
    }

    [Fact]
    public async Task PutsTheWeekAndTheCycleOnLocalMidnightAcrossTheEndOfDaylightSavingTime()
    {
        var w = new World();
        // Thursday 22 October 2026; clocks go back on Sunday 25 October, so the week of 19 to 25 October has 169 hours.
        w.Clock.Now = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        w.Earn(P2, "2026-10-18", 100); // the Sunday before: another week
        w.Earn(P2, "2026-10-19", 1); // Monday, local midnight 22:00Z the day before
        w.Earn(P2, "2026-10-25", 2); // Sunday, the last day of the week
        w.Earn(P2, "2026-10-26", 4); // the next Monday, local midnight 23:00Z the day before: the next week

        (await w.ProgressAsync(P2, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.Start == D("2026-10-19") && p.End == D("2026-10-25") && p.EarnedPoints == 3);
        (await w.ProgressAsync(P2, PeriodUnit.Cycle)).Should().Match<RewardProgress>(p => p.Start == D("2026-10-12") && p.End == D("2026-11-08") && p.EarnedPoints == 107);
        Midnight("2026-10-26").UtcDateTime.Should().Be(new DateTime(2026, 10, 25, 23, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ReadsThePeriodOfTodayInTheHouseholdTimezone_alsoAroundLocalMidnight()
    {
        var w = new World();
        // Sunday 20 September 22:30Z is already Monday 21 September 00:30 in Amsterdam: a new week.
        w.Clock.Now = new DateTimeOffset(2026, 9, 20, 22, 30, 0, TimeSpan.Zero);
        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.Start == D("2026-09-21") && p.End == D("2026-09-27"));
        w.Clock.Now = new DateTimeOffset(2026, 9, 20, 21, 30, 0, TimeSpan.Zero);
        (await w.ProgressAsync(P1, PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.Start == D("2026-09-14") && p.End == D("2026-09-20"));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData("00000000000000000000000g")]
    public async Task APersonIdThatIsNoIdIsAValidationErrorOnPersonId(string personId)
    {
        var w = new World();

        var result = await w.Service.ProgressAsync(new RewardProgressRequest(personId, PeriodUnit.Week), Ct);

        result.AsT1.Errors.Should().ContainKey("personId");
        w.Planned.Reads.Should().Be(0);
    }

    [Fact]
    public async Task AnUpperCasePersonIdIsReadAsTheSamePerson()
    {
        var w = new World().WithTheNodeHousehold();
        w.Earn("0000000000000000000000ab", "2026-09-14", 2);

        (await w.ProgressAsync("0000000000000000000000AB", PeriodUnit.Week)).Should().Match<RewardProgress>(p => p.PersonId == "0000000000000000000000ab" && p.EarnedPoints == 2);
    }

    [Fact]
    public async Task WithoutSettingsThereIsNothingToMeasure()
    {
        var w = new World();
        w.Settings.Document = null;

        (await w.Service.ProgressAsync(new RewardProgressRequest(P1, PeriodUnit.Week), Ct)).IsT2.Should().BeTrue();
    }

    [Fact]
    public async Task APortFailureIsReturnedAndNothingIsWritten()
    {
        var w = new World().WithTheNodeHousehold();
        var request = new RewardProgressRequest(P1, PeriodUnit.Week);

        w.Planned.Failure = new PortError("planned");
        (await w.Service.ProgressAsync(request, Ct)).AsT3.Message.Should().Be("planned");
        w.Planned.Failure = null;
        w.Ledger.Failure = new PortError("ledger");
        (await w.Service.ProgressAsync(request, Ct)).AsT3.Message.Should().Be("ledger");
        w.Ledger.Failure = null;
        w.Settings.Failure = new PortError("settings");
        (await w.Service.ProgressAsync(request, Ct)).AsT3.Message.Should().Be("settings");

        (w.Ledger.Writes, w.Ledger.BulkWrites, w.Settings.Writes).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task ARowThatCannotBeReadDoesNotHideTheMeter()
    {
        var w = new World().WithTheNodeHousehold();
        w.Planned.Items.Add(new PlannedWork(new BonusSource(w.Ledger.NextId(), null, Midnight("2026-09-15"), Midnight("2026-09-15"), false, P1, false, null, null, null), null, 5, null));

        (await w.ProgressAsync(P1, PeriodUnit.Week)).GoalPoints.Should().Be(4);
    }
}
