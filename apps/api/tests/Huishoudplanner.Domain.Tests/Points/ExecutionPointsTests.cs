using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;

namespace Huishoudplanner.Domain.Tests.Points;

/// <summary>
/// The pure rules of the execution entries (ADR-0011): which entry an occurrence is expected to leave and which snapshot work from before
/// snapshots gets. The direct tests of <c>expectedExecutionEntry</c> and the field migration of <c>reconcilePoints</c>.
/// </summary>
public sealed class ExecutionPointsTests
{
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private const string P1 = "0000000000000000000000a1";
    private const string P2 = "0000000000000000000000a2";
    private const string Task = "0000000000000000000000b1";

    private static DateTimeOffset At(string day) => DayKeys.FromDayKey(DayKeys.Parse(day), Amsterdam);

    private static ExecutionSource Source(
        string id = "0000000000000000000000c1",
        string? completedBy = P1,
        string? assignee = null,
        int? snapshot = 30,
        string day = "2026-09-16",
        OccurrenceStatus status = OccurrenceStatus.Done,
        string? taskId = Task,
        int? pointsOverride = null,
        int duration = 30) =>
        new(id, taskId, At(day), status, completedBy, assignee, snapshot, pointsOverride, duration, "Stofzuigen");

    // ---- the key

    [Fact]
    public void Key_isExecutionColonTheLowerCaseOccurrenceId() =>
        ExecutionPoints.Key("0000000000000000000000AB").Should().Be("execution:0000000000000000000000ab");

    // ---- expected entry

    [Fact]
    public void Expect_aDoneOccurrenceEarnsAnEntryForThePersonWhoDidTheWork()
    {
        var fields = ExecutionPoints.Expect(Source(completedBy: P2, assignee: P1), Amsterdam).Fields!;

        fields.Should().BeEquivalentTo(new
        {
            PersonId = P2,
            Amount = 30,
            Date = At("2026-09-16"),
            WeekStart = At("2026-09-14"),
            OccurrenceId = "0000000000000000000000c1",
            TaskId = Task,
            TitleSnapshot = "Stofzuigen",
        });
    }

    [Fact]
    public void Expect_olderDataWithoutCompletedByCreditsTheAssignee() =>
        ExecutionPoints.Expect(Source(completedBy: null, assignee: P1), Amsterdam).Fields!.PersonId.Should().Be(P1);

    [Theory]
    [InlineData(OccurrenceStatus.Open)]
    [InlineData(OccurrenceStatus.Skipped)]
    public void Expect_anOccurrenceThatIsNotDoneEarnsNothing(OccurrenceStatus status) =>
        ExecutionPoints.Expect(Source(status: status), Amsterdam).Should().Be(ExecutionExpectation.None);

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(null)]
    public void Expect_noPointsEarnsNothingAndIsNotUnattributed(int? snapshot) =>
        ExecutionPoints.Expect(Source(snapshot: snapshot, completedBy: null, assignee: null), Amsterdam).Should().Be(ExecutionExpectation.None);

    [Fact]
    public void Expect_workOfNobodyThatEarnedPointsIsUnattributedAndHasNoEntry()
    {
        var expectation = ExecutionPoints.Expect(Source(completedBy: null, assignee: null), Amsterdam);

        (expectation.Fields, expectation.Unattributed, expectation.Unreadable).Should().Be((null, true, false));
    }

    [Fact]
    public void Expect_aRowWithoutAReadableDateIsUnreadableNotFatal()
    {
        var expectation = ExecutionPoints.Expect(Source() with { Date = null }, Amsterdam);

        (expectation.Fields, expectation.Unattributed, expectation.Unreadable).Should().Be((null, false, true));
    }

    [Fact]
    public void Expect_aOneOffTaskKeepsANullTaskId() =>
        ExecutionPoints.Expect(Source(taskId: null), Amsterdam).Fields!.TaskId.Should().BeNull();

    [Theory]
    [InlineData("2026-09-14", "2026-09-14")] // Monday
    [InlineData("2026-09-20", "2026-09-14")] // Sunday still belongs to the week of that Monday
    [InlineData("2026-09-21", "2026-09-21")]
    [InlineData("2026-10-25", "2026-10-19")] // the day the clocks go back
    [InlineData("2026-03-29", "2026-03-23")] // the day the clocks go forward
    public void Expect_theWeekIsTheMondayOfTheDayInTheHouseholdTimezone(string day, string monday) =>
        ExecutionPoints.Expect(Source(day: day), Amsterdam).Fields!.WeekStart.Should().Be(At(monday));

    [Fact]
    public void Expect_theDayIsJudgedInTheHouseholdTimezoneNotUtc()
    {
        // Sunday 2026-09-20 23:30 in Amsterdam is still Sunday there, although it is 21:30 UTC; the Monday of that week is 14 September.
        var late = new DateTimeOffset(2026, 9, 20, 21, 30, 0, TimeSpan.Zero);
        var source = Source() with { Date = late };

        ExecutionPoints.Expect(source, Amsterdam).Fields!.WeekStart.Should().Be(At("2026-09-14"));
    }

    // ---- snapshots of work from before snapshots

    private static Dictionary<string, TaskPointValue> Tasks(params TaskPointValue[] tasks) => tasks.ToDictionary(t => t.Id);

    private static HashSet<string> None => new HashSet<string>();

    [Fact]
    public void MissingSnapshots_aOneOffTaskGetsThePointsItWasRecordedWithEvenWhenThatIsZero()
    {
        var done = new[]
        {
            Source("0000000000000000000000d1", snapshot: null, taskId: null, pointsOverride: 7, duration: 40),
            Source("0000000000000000000000d2", snapshot: null, taskId: null, pointsOverride: 0, duration: 40),
        };

        var writes = ExecutionPoints.MissingSnapshots(done, Tasks(), None);

        writes.Should().Equal(new SnapshotWrite(done[0].Id, 7), new SnapshotWrite(done[1].Id, 0));
    }

    [Fact]
    public void MissingSnapshots_aOneOffTaskWithoutPointsTakesTheDurationRule() =>
        ExecutionPoints.MissingSnapshots([Source(snapshot: null, taskId: null, duration: 95)], Tasks(), None).Single().Points.Should().Be(95);

    [Fact]
    public void MissingSnapshots_aTaskThatNoLongerExistsTakesTheDurationRuleOfTheOccurrence() =>
        ExecutionPoints.MissingSnapshots([Source(snapshot: null, duration: 25)], Tasks(), None).Single().Points.Should().Be(25);

    [Fact]
    public void MissingSnapshots_aTaskWithAnExplicitValueGivesItsPoints() =>
        ExecutionPoints.MissingSnapshots([Source(snapshot: null, duration: 15)], Tasks(new TaskPointValue(Task, 9, 60)), None).Single().Points.Should().Be(9);

    [Fact]
    public void MissingSnapshots_aTaskWhosePointsWereJustFilledInGivesTheDurationTheOccurrenceHadNotTheTasksCurrentOne() =>
        ExecutionPoints
            .MissingSnapshots([Source(snapshot: null, duration: 15)], Tasks(new TaskPointValue(Task, 60, 60)), new HashSet<string> { Task })
            .Single().Points.Should().Be(15);

    [Fact]
    public void MissingSnapshots_aTaskStillWithoutPointsFallsBackToItsOwnDuration() =>
        ExecutionPoints.MissingSnapshots([Source(snapshot: null, duration: 15)], Tasks(new TaskPointValue(Task, null, 45)), None).Single().Points.Should().Be(45);

    [Fact]
    public void MissingSnapshots_theDurationRuleIsClampedToTheTaskPointsRange()
    {
        var writes = ExecutionPoints.MissingSnapshots(
            [Source("0000000000000000000000d1", snapshot: null, taskId: null, duration: 5000), Source("0000000000000000000000d2", snapshot: null, taskId: null, duration: 0)],
            Tasks(),
            None);

        writes.Select(w => w.Points).Should().Equal(1000, 1);
    }

    [Fact]
    public void MissingSnapshots_anOccurrenceThatAlreadyHasASnapshotIsNeverTouchedSoTaskChangesNeverRewriteIt() =>
        ExecutionPoints.MissingSnapshots([Source(snapshot: 30)], Tasks(new TaskPointValue(Task, 9, 60)), None).Should().BeEmpty();
}
