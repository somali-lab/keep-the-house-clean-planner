using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Statistics;
using static Huishoudplanner.Domain.Tests.Statistics.StatisticsWorld;

namespace Huishoudplanner.Domain.Tests.Statistics;

/// <summary>Ports the report scenarios of <c>apps/server/test/stats.test.ts</c> as direct tests of the pure calculation.</summary>
public sealed class StatisticsCalculatorTests
{
    private static int[] Minutes(IEnumerable<UserWorkload> users, string id) =>
        users.Where(u => u.UserId == id).Select(u => new[] { u.PlannedMinutes, u.DoneMinutes }).Single();

    // ---- scope

    [Fact]
    public void Scope_ofTwoCycles_isTheCurrentAndThePreviousOne_oldestFirst()
    {
        var scope = Scope(cycles: 2);

        scope.Cycles.Select(c => c.Index).Should().Equal(0, 1);
    }

    [Fact]
    public void Scope_ofOneCycle_isTheCurrentOne()
    {
        Scope(cycles: 1).Cycles.Select(c => c.Index).Should().Equal(1);
    }

    [Fact]
    public void Scope_neverIncludesACycleThatHasNotStarted()
    {
        var scope = Scope(cycles: 26, now: Instant("2026-09-20T08:00:00Z"));

        scope.Cycles.Select(c => c.Index).Should().Equal(0);
    }

    [Fact]
    public void Scope_ofWeeks_coversTheCyclesThatTouchThoseWeeks_andCutsAtTheMondays()
    {
        var scope = Scope(weeks: 3);

        scope.Cycles.Select(c => c.Index).Should().Equal(0, 1);
        scope.FromKey.Should().Be(Day("2026-09-28"));
        scope.ToKey.Should().Be(Day("2026-10-19"));
    }

    // ---- workload

    [Fact]
    public void Workload_reportsPlannedByAssigneeAndDoneByCompletedBy_perWeekAndCycle()
    {
        var report = StatisticsCalculator.Workload(Scope(cycles: 2), AllOccurrences, People);

        report.Cycles.Select(c => (c.Index, c.StartDate, c.EndDate)).Should().Equal(
            (0, Day("2026-09-14"), Day("2026-10-11")),
            (1, Day("2026-10-12"), Day("2026-11-08")));
        var (c0, c1) = (report.Cycles[0], report.Cycles[1]);
        c0.Weeks.Select(w => w.StartDate).Should().Equal(Day("2026-09-14"), Day("2026-09-21"), Day("2026-09-28"), Day("2026-10-05"));
        c0.Weeks.Select(w => Minutes(w.Users, P1)).Should().BeEquivalentTo(new int[][] { [30, 30], [75, 45], [30, 0], [30, 0] }, o => o.WithStrictOrdering());
        c0.Weeks.Select(w => Minutes(w.Users, P2)).Should().BeEquivalentTo(new int[][] { [20, 20], [0, 30], [20, 20], [0, 0] }, o => o.WithStrictOrdering());
        Minutes(c0.Users, P1).Should().Equal(165, 75);
        Minutes(c0.Users, P2).Should().Equal(40, 70);
        c0.UnassignedPlannedMinutes.Should().Be(0);
        c1.Weeks.Select(w => Minutes(w.Users, P1)).Should().BeEquivalentTo(new int[][] { [30, 30], [30, 0], [30, 0], [30, 0] }, o => o.WithStrictOrdering());
        Minutes(c1.Users, P1).Should().Equal(120, 30);
        Minutes(c1.Users, P2).Should().Equal(40, 0);
        c1.UnassignedPlannedMinutes.Should().Be(45);
        c1.Weeks.Select(w => w.UnassignedPlannedMinutes).Should().Equal(0, 45, 0, 0);
    }

    [Fact]
    public void Workload_limitsToTheRequestedNumberOfRecentCycles()
    {
        StatisticsCalculator.Workload(Scope(cycles: 1), AllOccurrences, People).Cycles.Select(c => c.Index).Should().Equal(1);
    }

    [Fact]
    public void Workload_ofThisWeek_hasOneWeek()
    {
        var report = StatisticsCalculator.Workload(Scope(weeks: 1), AllOccurrences, People);

        report.Cycles.Should().ContainSingle().Which.Weeks.Select(w => w.StartDate).Should().Equal(Day("2026-10-12"));
        report.Cycles[0].Users.Should().OnlyContain(u => u.PlannedMinutes >= 0);
    }

    [Fact]
    public void Workload_ofTheLastThreeWeeks_spansTheCycleBoundary()
    {
        var report = StatisticsCalculator.Workload(Scope(weeks: 3), AllOccurrences, People);

        report.Cycles.SelectMany(c => c.Weeks.Select(w => w.StartDate)).Should().Equal(Day("2026-09-28"), Day("2026-10-05"), Day("2026-10-12"));
    }

    [Fact]
    public void Workload_countsOnlyTheOccurrencesOfThePeriod_inACycleThatIsCutOff()
    {
        var report = StatisticsCalculator.Workload(Scope(weeks: 1), AllOccurrences, People);

        // Of cycle 1 only the week of 12 Oct is in the period: A (30, done by P1) and B (20) on Thursday 15 Oct.
        Minutes(report.Cycles[0].Users, P1).Should().Equal(30, 30);
        Minutes(report.Cycles[0].Users, P2).Should().Equal(20, 0);
    }

    [Fact]
    public void Workload_listsAnInactivePerson_onlyWhenTheyAppearInThePeriod()
    {
        var gone = new StatisticsPerson("person-gone", "Weg", false);
        var inactiveButPlanned = new StatisticsPerson(P2, "Persoon 2", false);

        var report = StatisticsCalculator.Workload(Scope(cycles: 2), AllOccurrences, [new(P1, "Persoon 1", true), inactiveButPlanned, gone]);

        report.Cycles[0].Users.Select(u => u.UserId).Should().Equal(P1, P2);
    }

    [Fact]
    public void Workload_countsOneOffTasksAsPlannedAndDoneMinutes()
    {
        var oneOffs = AllOccurrences.Concat(
        [
            Occ(Cycle1, null, "2026-10-14", P2, OccurrenceStatus.Done, "2026-10-14T08:00:00Z", P2, minutes: 40, name: "Gordijnen ophangen", room: Woonkamer),
            Occ(Cycle1, null, "2026-10-14", P1, OccurrenceStatus.Done, "2026-10-14T08:00:00Z", P1, minutes: 25, name: "Kast ophalen"),
        ]).ToList();

        var users = StatisticsCalculator.Workload(Scope(cycles: 2), oneOffs, People).Cycles[1].Users;

        Minutes(users, P1).Should().Equal(120 + 25, 30 + 25);
        Minutes(users, P2).Should().Equal(40 + 40, 40);
    }

    [Fact]
    public void Workload_countsOneOffWorkWithoutAssigneeAsUnassigned()
    {
        var oneOffs = AllOccurrences.Append(Occ(Cycle1, null, "2026-10-14", null, OccurrenceStatus.Open, minutes: 25, name: "Kast ophalen")).ToList();

        StatisticsCalculator.Workload(Scope(cycles: 2), oneOffs, People).Cycles[1].UnassignedPlannedMinutes.Should().Be(45 + 25);
    }

    [Fact]
    public void Workload_ofAnEmptyScope_isEmpty()
    {
        var report = StatisticsCalculator.Workload(Scope(cycles: 4, now: Instant("2026-08-01T08:00:00Z")), AllOccurrences, People);

        report.Cycles.Should().BeEmpty();
    }

    [Fact]
    public void Workload_ofAWeekWithOnlyOneOffWork_isAnswered()
    {
        var onlyOneOff = new[] { Occ(Cycle1, null, "2026-10-14", P1, OccurrenceStatus.Done, "2026-10-14T08:00:00Z", P1, minutes: 25, name: "Kast ophalen") };

        var report = StatisticsCalculator.Workload(Scope(weeks: 1), onlyOneOff, People);

        Minutes(report.Cycles.Single().Users, P1).Should().Equal(25, 25);
    }

    // ---- completion

    [Fact]
    public void Completion_byTask_isDoneOverDoneSkippedAndMissed_worstFirst()
    {
        var report = StatisticsCalculator.Completion(Scope(cycles: 2), AllOccurrences, StatsGroupBy.Task, StartOfToday(), AllTasks, AllRooms, People);

        report.GroupBy.Should().Be("task");
        report.Rows.Should().Equal(
            new CompletionRow(A, "Badkamer schoonmaken", 3, 1, 1, 0.6),
            new CompletionRow(B, "Keuken dweilen", 2, 0, 0, 1),
            new CompletionRow(C, "Ramen lappen", 1, 0, 0, 1));
    }

    [Fact]
    public void Completion_byRoom_usesTheRoomOfTheTask()
    {
        var report = StatisticsCalculator.Completion(Scope(cycles: 2), AllOccurrences, StatsGroupBy.Room, StartOfToday(), AllTasks, AllRooms, People);

        report.Rows.Select(r => (r.Name, r.Done, r.Skipped, r.Missed, r.Rate)).Should().Equal(
            ("Badkamer", 3, 1, 1, (double?)0.6),
            ("Keuken", 2, 0, 0, 1),
            ("Woonkamer", 1, 0, 0, 1));
    }

    [Fact]
    public void Completion_byUser_groupsByAssignee()
    {
        var report = StatisticsCalculator.Completion(Scope(cycles: 2), AllOccurrences, StatsGroupBy.User, StartOfToday(), AllTasks, AllRooms, People);

        report.Rows.Should().HaveCount(2);
        report.Rows[0].Should().BeEquivalentTo(new CompletionRow(P1, "Persoon 1", 4, 1, 1, 4 / 6.0));
        report.Rows[1].Should().Be(new CompletionRow(P2, "Persoon 2", 2, 0, 0, 1));
    }

    [Fact]
    public void Completion_ofAWeek_appliesThePeriodToTheResult()
    {
        var report = StatisticsCalculator.Completion(Scope(weeks: 1), AllOccurrences, StatsGroupBy.Task, StartOfToday(), AllTasks, AllRooms, People);

        report.Rows.Should().Equal(new CompletionRow(A, "Badkamer schoonmaken", 1, 0, 0, 1));
    }

    [Fact]
    public void Completion_doesNotCountOpenWorkOfToday_orOfTheFuture()
    {
        var report = StatisticsCalculator.Completion(Scope(cycles: 2), AllOccurrences, StatsGroupBy.Task, StartOfToday(Instant("2026-10-12T08:00:00Z")), AllTasks, AllRooms, People);

        // On Monday 12 Oct the open work of 5 Oct is the only missed work; the open work of 12 Oct itself is done, later work is not due.
        report.Rows.Single(r => r.Key == A).Missed.Should().Be(1);
    }

    [Fact]
    public void Completion_showsAllOneOffTasksAsOneRowWithoutAKey_whenGroupedByTask()
    {
        var withOneOffs = OneOffs();

        var rows = StatisticsCalculator.Completion(Scope(cycles: 2), withOneOffs, StatsGroupBy.Task, StartOfToday(), AllTasks, AllRooms, People).Rows;

        rows.Where(r => r.Key is null).Should().Equal(new CompletionRow(null, string.Empty, 2, 0, 0, 1));
        rows.Where(r => r.Key is not null).Select(r => r.Name).Order().Should().Equal("Badkamer schoonmaken", "Keuken dweilen", "Ramen lappen");
    }

    [Fact]
    public void Completion_groupsOneOffTasksByTheRoomSnapshot_andRoomlessOnesInOneRow()
    {
        var rows = StatisticsCalculator.Completion(Scope(cycles: 2), OneOffs(), StatsGroupBy.Room, StartOfToday(), AllTasks, AllRooms, People).Rows;

        rows.Where(r => r.Key is not null).ToDictionary(r => r.Name, r => r.Done).Should().Contain(new Dictionary<string, int> { ["Badkamer"] = 3, ["Keuken"] = 2, ["Woonkamer"] = 2 });
        rows.Single(r => r.Key is null).Done.Should().Be(1);
    }

    [Fact]
    public void Completion_countsOneOffTasksForThePersonWhoDidThem()
    {
        var rows = StatisticsCalculator.Completion(Scope(cycles: 2), OneOffs(), StatsGroupBy.User, StartOfToday(), AllTasks, AllRooms, People).Rows;

        rows.Single(r => r.Key == P1).Done.Should().Be(5);
        rows.Single(r => r.Key == P2).Done.Should().Be(3);
    }

    [Fact]
    public void Completion_ofWeekWithOnlyOneOffWork_isAnsweredForEveryGrouping()
    {
        var onlyOneOff = new[] { Occ(Cycle1, null, "2026-10-14", P1, OccurrenceStatus.Done, "2026-10-14T08:00:00Z", P1, name: "Kast ophalen") };

        foreach (var groupBy in Enum.GetValues<StatsGroupBy>())
        {
            StatisticsCalculator.Completion(Scope(weeks: 1), onlyOneOff, groupBy, StartOfToday(), AllTasks, AllRooms, People).Rows.Should().ContainSingle();
        }
    }

    [Fact]
    public void Completion_fallsBackToTheSnapshotName_whenTheTaskIsNotInTheDirectory()
    {
        var rows = StatisticsCalculator.Completion(Scope(cycles: 2), AllOccurrences, StatsGroupBy.Task, StartOfToday(), [], AllRooms, People).Rows;

        rows.Select(r => r.Name).Should().Contain("Badkamer schoonmaken");
    }

    [Fact]
    public void Completion_sortsRowsWithoutAnythingDueLast_andNamesByDutchCollation()
    {
        var occurrences = new[]
        {
            Occ(Cycle1, A, "2026-10-12", P1, OccurrenceStatus.Done, "2026-10-12T18:00:00Z", P1),
            Occ(Cycle1, B, "2026-10-12", P1, OccurrenceStatus.Done, "2026-10-12T18:00:00Z", P1),
        };
        var tasks = new StatisticsTask[] { new(A, "Zolder", Badkamer, "1w", true), new(B, "Ãbc", Badkamer, "1w", true) };

        var rows = StatisticsCalculator.Completion(Scope(cycles: 1), occurrences, StatsGroupBy.Task, StartOfToday(), tasks, AllRooms, People).Rows;

        rows.Select(r => r.Name).Should().Equal("Ãbc", "Zolder");
    }

    // ---- intervals

    [Fact]
    public void Intervals_compareTheAverageDaysBetweenCompletionsWithThePeriod_mostDeviatingFirst()
    {
        var rows = StatisticsCalculator.Intervals(Scope(cycles: 2), AllOccurrences, AllTasks, Intervals).Rows;

        rows.Select(r => (r.Name, r.PeriodDays, r.Completions, r.AverageDays)).Should().Equal(
            ("Badkamer schoonmaken", 7, 3, (double?)14),
            ("Keuken dweilen", 14, 2, 15),
            ("Ramen lappen", 28, 1, null));
        rows[0].Deviation.Should().Be(2);
        rows[1].Deviation.Should().BeApproximately(15 / 14.0, 1e-12);
        rows[2].Deviation.Should().BeNull();
    }

    [Fact]
    public void Intervals_leaveOutOneOffTasks()
    {
        var rows = StatisticsCalculator.Intervals(Scope(cycles: 2), OneOffs(), AllTasks, Intervals).Rows;

        rows.Select(r => (r.Name, r.Completions)).Should().Equal(("Badkamer schoonmaken", 3), ("Keuken dweilen", 2), ("Ramen lappen", 1));
    }

    [Fact]
    public void Intervals_listAnInactiveTask_onlyWhenItWasCompletedInThePeriod()
    {
        var tasks = new StatisticsTask[] { new(A, "Badkamer schoonmaken", Badkamer, "1w", false), new(B, "Keuken dweilen", Keuken, "2wk", true), new("task-d", "Oud", Keuken, "1w", false) };

        var rows = StatisticsCalculator.Intervals(Scope(cycles: 2), AllOccurrences, tasks, Intervals).Rows;

        rows.Select(r => r.TaskId).Should().Equal(A, B);
    }

    [Fact]
    public void Intervals_measureTheGapInLocalCalendarDays_acrossTheEndOfDaylightSaving()
    {
        // 24 Oct 22:30 UTC is 00:30 on 25 Oct local time (summer time); 26 Oct 00:30 UTC is 01:30 local on 26 Oct (winter time, a 25-hour day).
        var occurrences = new[]
        {
            Occ(Cycle1, A, "2026-10-24", P1, OccurrenceStatus.Done, "2026-10-24T22:30:00Z", P1),
            Occ(Cycle1, A, "2026-10-26", P1, OccurrenceStatus.Done, "2026-10-26T00:30:00Z", P1),
        };

        var row = StatisticsCalculator.Intervals(Scope(cycles: 1, now: Instant("2026-11-01T08:00:00Z")), occurrences, AllTasks, Intervals).Rows.Single(r => r.TaskId == A);

        row.AverageDays.Should().Be(1);
    }

    [Fact]
    public void Intervals_withoutACycle_stillListTheActiveTasksWithoutCompletions()
    {
        var rows = StatisticsCalculator.Intervals(Scope(cycles: 4, now: Instant("2026-08-01T08:00:00Z")), AllOccurrences, AllTasks, Intervals).Rows;

        rows.Should().HaveCount(3).And.OnlyContain(r => r.Completions == 0 && r.AverageDays == null && r.Deviation == null);
    }

    [Fact]
    public void Intervals_ofAWeekWithOnlyOneOffWork_isAnswered()
    {
        StatisticsCalculator.Intervals(Scope(weeks: 1), [Occ(Cycle1, null, "2026-10-14", P1, OccurrenceStatus.Done, "2026-10-14T08:00:00Z", P1)], AllTasks, Intervals).Rows.Should().HaveCount(3);
    }

    // ---- deviations

    [Fact]
    public void Deviations_separatePlanChangesFromEarlyOrLateCompletion()
    {
        var rows = StatisticsCalculator.Deviations(Scope(cycles: 2), AllOccurrences).Rows;

        rows.Should().Equal(
            new DeviationRow(B, "Keuken dweilen", 2, 0, 0.5, 0, 1, 1),
            new DeviationRow(C, "Ramen lappen", 1, 1, 0, 0, 1, 0),
            new DeviationRow(A, "Badkamer schoonmaken", 3, 0, 0, 0, 3, 0));
    }

    [Fact]
    public void Deviations_countACompletionBeforeItsDayAsEarly()
    {
        var occurrences = new[] { Occ(Cycle1, A, "2026-10-13", P1, OccurrenceStatus.Done, "2026-10-12T10:00:00Z", P1) };

        var row = StatisticsCalculator.Deviations(Scope(cycles: 1), occurrences).Rows.Single();

        (row.Early, row.OnTime, row.Late, row.AverageCompletionDelayDays).Should().Be((1, 0, 0, -1.0));
    }

    [Fact]
    public void Deviations_leaveOutOneOffTasks_andRecordedExtraWork()
    {
        var occurrences = OneOffs().Append(Occ(Cycle1, A, "2026-10-14", P1, OccurrenceStatus.Done, "2026-10-14T08:00:00Z", P1, recorded: true)).ToList();

        var rows = StatisticsCalculator.Deviations(Scope(cycles: 2), occurrences).Rows;

        rows.Should().HaveCount(3);
        rows.Single(r => r.TaskId == A).Completions.Should().Be(3);
    }

    [Fact]
    public void Deviations_ofAWeekWithOnlyOneOffWork_isEmpty()
    {
        StatisticsCalculator.Deviations(Scope(weeks: 1), [Occ(Cycle1, null, "2026-10-14", P1, OccurrenceStatus.Done, "2026-10-14T08:00:00Z", P1)]).Rows.Should().BeEmpty();
    }

    [Fact]
    public void Deviations_withoutACycle_areEmpty()
    {
        StatisticsCalculator.Deviations(Scope(cycles: 4, now: Instant("2026-08-01T08:00:00Z")), AllOccurrences).Rows.Should().BeEmpty();
    }

    private static List<StatisticsOccurrence> OneOffs() =>
    [
        .. AllOccurrences,
        Occ(Cycle1, null, "2026-10-14", P2, OccurrenceStatus.Done, "2026-10-14T08:00:00Z", P2, minutes: 40, name: "Gordijnen ophangen", room: Woonkamer),
        Occ(Cycle1, null, "2026-10-14", P1, OccurrenceStatus.Done, "2026-10-14T08:00:00Z", P1, minutes: 25, name: "Kast ophalen"),
    ];
}
