using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Statistics;
using Moq;
using static Huishoudplanner.Application.Tests.Statistics.StatisticsWorld;

namespace Huishoudplanner.Application.Tests.Statistics;

public sealed class StatisticsServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- period validation

    [Theory]
    [InlineData(0, null, "cycles")]
    [InlineData(27, null, "cycles")]
    [InlineData(-1, null, "cycles")]
    [InlineData(null, 0, "weeks")]
    [InlineData(null, 4, "weeks")]
    public async Task Reports_refuseAPeriodOutOfRange_withAFieldKeyedError(int? cycles, int? weeks, string field)
    {
        var world = new StatisticsWorld();

        var results = new object[]
        {
            await world.Service.WorkloadAsync(cycles, weeks, Ct),
            await world.Service.IntervalsAsync(cycles, weeks, Ct),
            await world.Service.DeviationsAsync(cycles, weeks, Ct),
            await world.Service.CompletionAsync(cycles, weeks, StatsGroupBy.Task, Ct),
        };

        foreach (var result in results)
        {
            AsValidation(result).Errors.Keys.Should().Equal(field);
        }

        world.Reader.OccurrenceReads.Should().Be(0);
    }

    [Fact]
    public async Task Reports_defaultToFourCycles_andAcceptTheMaximumPeriods()
    {
        var world = new StatisticsWorld();

        (await world.Service.WorkloadAsync(null, null, Ct)).IsT0.Should().BeTrue();
        (await world.Service.WorkloadAsync(26, null, Ct)).IsT0.Should().BeTrue();
        (await world.Service.WorkloadAsync(null, 3, Ct)).IsT0.Should().BeTrue();
    }

    [Fact]
    public async Task Reports_withoutSettings_answerSettingsMissing()
    {
        var world = new StatisticsWorld { Settings = null };

        (await world.Service.WorkloadAsync(2, null, Ct)).IsT2.Should().BeTrue();
        (await world.Service.IntervalsAsync(2, null, Ct)).IsT2.Should().BeTrue();
    }

    [Fact]
    public async Task Reports_surfaceAnInfrastructureFailure_asAPortError()
    {
        var world = new StatisticsWorld();
        world.Reader.Failure = new PortError("occurrences.failed");

        (await world.Service.WorkloadAsync(2, null, Ct)).AsT3.Message.Should().Be("occurrences.failed");
        (await world.Service.CompletionAsync(2, null, StatsGroupBy.Room, Ct)).IsT3.Should().BeTrue();
        (await world.Service.IntervalsAsync(2, null, Ct)).IsT3.Should().BeTrue();
        (await world.Service.DeviationsAsync(2, null, Ct)).IsT3.Should().BeTrue();
    }

    [Fact]
    public async Task Reports_surfaceASettingsFailure_asAPortError()
    {
        var world = new StatisticsWorld { SettingsFailure = new PortError("settings.failed") };

        (await world.Service.WorkloadAsync(2, null, Ct)).AsT3.Message.Should().Be("settings.failed");
    }

    // ---- reading

    [Fact]
    public async Task Workload_readsTheOccurrencesOfTheSelectedCycles_only()
    {
        var world = new StatisticsWorld();

        await world.Service.WorkloadAsync(1, null, Ct);

        world.Reader.AskedCycleIds.Should().Equal(Cycle1);
        world.Reader.AskedFrom.Should().BeNull();
        world.Reader.AskedTo.Should().BeNull();
    }

    [Fact]
    public async Task Workload_ofWeeks_asksForTheWeekRangeInTheHouseholdTimezone()
    {
        var world = new StatisticsWorld();

        await world.Service.WorkloadAsync(null, 3, Ct);

        world.Reader.AskedCycleIds.Should().Equal(Cycle0, Cycle1);
        world.Reader.AskedFrom.Should().Be(new DateTimeOffset(2026, 9, 27, 22, 0, 0, TimeSpan.Zero), "midnight of Monday 28 September in Amsterdam");
        world.Reader.AskedTo.Should().Be(new DateTimeOffset(2026, 10, 18, 22, 0, 0, TimeSpan.Zero), "midnight of Monday 19 October in Amsterdam");
    }

    [Fact]
    public async Task Reports_withoutAGeneratedCycle_readNoOccurrences_andAreEmpty()
    {
        var world = new StatisticsWorld { Cycles = [] };

        (await world.Service.WorkloadAsync(4, null, Ct)).AsT0.Cycles.Should().BeEmpty();
        (await world.Service.CompletionAsync(4, null, StatsGroupBy.User, Ct)).AsT0.Rows.Should().BeEmpty();
        (await world.Service.DeviationsAsync(4, null, Ct)).AsT0.Rows.Should().BeEmpty();
        world.Reader.OccurrenceReads.Should().Be(0);
    }

    [Fact]
    public async Task Cycles_arePagedThrough_untilTheLastOneIsRead()
    {
        var world = new StatisticsWorld();
        var anchor = new DateOnly(2026, 9, 14);
        world.Cycles = [.. Enumerable.Range(-300, 301).Select(i => new Huishoudplanner.Domain.Generation.Cycle($"{i + 400:x24}", i, anchor.AddDays(i * 28), anchor.AddDays((i * 28) + 27), null, Now, "run"))];
        world.Cycles.Add(new(Cycle1, 1, new DateOnly(2026, 10, 12), new DateOnly(2026, 11, 8), null, Now, "run"));

        var result = await world.Service.WorkloadAsync(26, null, Ct);

        result.AsT0.Cycles.Should().HaveCount(26);
        result.AsT0.Cycles[^1].Index.Should().Be(1);
        world.CycleStore.Verify(c => c.ListAsync(It.IsAny<Huishoudplanner.Domain.Generation.CycleCursor?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ---- reports

    [Fact]
    public async Task Workload_calculatesPlannedAndDoneMinutes()
    {
        var world = new StatisticsWorld();
        world.Reader.Occurrences.Add(Occurrence(Cycle1, TaskA, new DateOnly(2026, 10, 12), P1, OccurrenceStatus.Done, 30, P1));
        world.Reader.Occurrences.Add(Occurrence(Cycle1, TaskA, new DateOnly(2026, 10, 19), P1, OccurrenceStatus.Open, 30));

        var report = (await world.Service.WorkloadAsync(1, null, Ct)).AsT0;

        report.Cycles.Single().Users.Single(u => u.UserId == P1).Should().Be(new UserWorkload(P1, 60, 30));
    }

    [Fact]
    public async Task Completion_countsOpenWorkOfEarlierDaysAsMissed_usingTheClock()
    {
        var world = new StatisticsWorld();
        world.Reader.Occurrences.Add(Occurrence(Cycle1, TaskA, new DateOnly(2026, 10, 12), P1, OccurrenceStatus.Open));
        world.Reader.Occurrences.Add(Occurrence(Cycle1, TaskA, new DateOnly(2026, 10, 14), P1, OccurrenceStatus.Open));
        world.Reader.Occurrences.Add(Occurrence(Cycle1, TaskA, new DateOnly(2026, 10, 15), P1, OccurrenceStatus.Open));

        var report = (await world.Service.CompletionAsync(1, null, StatsGroupBy.Task, Ct)).AsT0;

        report.GroupBy.Should().Be("task");
        report.Rows.Should().Equal(new CompletionRow(TaskA, "Badkamer schoonmaken", 0, 0, 1, 0));
    }

    [Fact]
    public async Task Intervals_takeThePeriodOfEachIntervalFromTheSettings()
    {
        var world = new StatisticsWorld();

        var report = (await world.Service.IntervalsAsync(1, null, Ct)).AsT0;

        report.Rows.Should().ContainSingle().Which.PeriodDays.Should().Be(7);
    }

    // ---- reset

    [Fact]
    public async Task Reset_withoutBefore_startsOver_andRecordsOneAuditEntryInTheSameTransaction()
    {
        var world = new StatisticsWorld();

        var result = await world.Service.ResetAsync(Admin, null, Ct);

        result.AsT0.Should().Be(FakeResetter.Counts);
        world.Resetter.Plans.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            RestartFromToday = true,
            BoundaryKey = new DateOnly(2026, 10, 14),
            BoundaryCycle = 1,
            BonusFloor = new DateOnly(2026, 10, 14),
            BonusFloorBefore = (DateOnly?)null,
        });
        world.Transactions.Runs.Should().Be(1);
        world.Transactions.Aborts.Should().Be(0);
        world.Resetter.AuditEntriesWhenRun.Should().Be(0);
        var entry = world.Audit.Entries.Should().ContainSingle().Subject;
        entry.Actor.Should().Be(AuditActor.From(Admin));
        entry.Entity.Should().Be(AuditEntity.Settings);
        entry.EntityId.Should().Be(SettingsIds.Singleton);
        entry.Action.Should().Be(AuditAction.Reset);
        entry.After["statistics"].Should().Be(new AuditString("opnieuw gestart"));
        entry.Meta!["removedPointEntries"].Should().Be(new AuditInteger(6));
        entry.Meta["scoped"].Should().Be(new AuditBool(false));
        entry.Meta["resetId"].Should().BeOfType<AuditString>().Which.Value.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_thatRemovesNothing_answersTheZeroCounts_butWritesAndAuditsNothing(bool purge)
    {
        var world = new StatisticsWorld();
        world.Resetter.Result = new StatisticsResetResult(0, 0, 0, 0, 0, 0, 0);

        var result = await world.Service.ResetAsync(Admin, purge ? new DateOnly(2026, 10, 12) : null, Ct);

        result.AsT0.Should().Be(new StatisticsResetResult(0, 0, 0, 0, 0, 0, 0));
        world.Audit.Entries.Should().BeEmpty();
        world.Transactions.Aborts.Should().Be(1, "the transaction rolls back whatever the store wrote, such as the bonus floor");
    }

    [Fact]
    public async Task Reset_thatRemovesOnlyOneKindOfThing_isStillRecorded()
    {
        var world = new StatisticsWorld();
        world.Resetter.Result = new StatisticsResetResult(0, 0, 0, 0, 0, 0, 1);

        (await world.Service.ResetAsync(Admin, null, Ct)).IsT0.Should().BeTrue();

        world.Audit.Entries.Should().ContainSingle();
        world.Transactions.Aborts.Should().Be(0);
    }

    [Fact]
    public async Task Reset_withBefore_purgesOnlyOlderData_andIsScoped()
    {
        var world = new StatisticsWorld();

        var result = await world.Service.ResetAsync(Admin, new DateOnly(2026, 10, 12), Ct);

        result.IsT0.Should().BeTrue();
        var plan = world.Resetter.Plans.Should().ContainSingle().Subject;
        plan.RestartFromToday.Should().BeFalse();
        plan.BoundaryCycle.Should().Be(1);
        plan.Boundary.Should().Be(new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero));
        world.Audit.Entries.Single().Meta!["scoped"].Should().Be(new AuditBool(true));
    }

    [Fact]
    public async Task Reset_withBeforeInTheFuture_isRefused_andWritesAndAuditsNothing()
    {
        var world = new StatisticsWorld();

        var result = await world.Service.ResetAsync(Admin, new DateOnly(2026, 10, 15), Ct);

        result.IsT1.Should().BeTrue();
        world.Resetter.Plans.Should().BeEmpty();
        world.Audit.Entries.Should().BeEmpty();
        world.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Reset_movesTheBonusFloorOnlyForward()
    {
        var world = new StatisticsWorld();
        world.Settings = world.Settings! with { BonusFloor = new DateOnly(2026, 10, 14) };

        await world.Service.ResetAsync(Admin, new DateOnly(2026, 10, 1), Ct);

        var plan = world.Resetter.Plans.Single();
        plan.MovesBonusFloor.Should().BeFalse();
        world.Audit.Entries.Single().After["bonusFloor"].Should().Be(new AuditString("2026-10-14"));
        world.Audit.Entries.Single().Before["bonusFloor"].Should().Be(new AuditString("2026-10-14"));
    }

    [Fact]
    public async Task Reset_withoutSettings_answersSettingsMissing_andAbortsTheTransaction()
    {
        var world = new StatisticsWorld { Settings = null };

        var result = await world.Service.ResetAsync(Admin, null, Ct);

        result.IsT2.Should().BeTrue();
        world.Resetter.Plans.Should().BeEmpty();
        world.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Reset_whenTheStoreFails_aborts_andLeavesNoAuditEntry()
    {
        var world = new StatisticsWorld();
        world.Resetter.Failure = new PortError("statistics.failed");

        var result = await world.Service.ResetAsync(Admin, null, Ct);

        result.AsT4.Message.Should().Be("statistics.failed");
        world.Audit.Entries.Should().BeEmpty();
        world.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Reset_whenTheAuditFails_aborts_soTheWritesAreRolledBack()
    {
        var world = new StatisticsWorld();
        world.Audit.Failure = new PortError("audit.failed");

        var result = await world.Service.ResetAsync(Admin, null, Ct);

        result.AsT4.Message.Should().Be("audit.failed");
        world.Transactions.Aborts.Should().Be(1);
        world.Transactions.Runs.Should().Be(1);
    }

    [Fact]
    public async Task Reset_whenConcurrentWritersKeepWinning_answersAConflict()
    {
        var world = new StatisticsWorld();
        world.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "Try again.");

        var result = await world.Service.ResetAsync(Admin, null, Ct);

        result.AsT3.Code.Should().Be("write_conflict");
        world.Resetter.Plans.Should().BeEmpty();
    }

    private static ValidationErrors AsValidation(object result) => result switch
    {
        OneOf.OneOf<WorkloadReport, ValidationErrors, SettingsMissing, PortError> r => r.AsT1,
        OneOf.OneOf<IntervalReport, ValidationErrors, SettingsMissing, PortError> r => r.AsT1,
        OneOf.OneOf<DeviationReport, ValidationErrors, SettingsMissing, PortError> r => r.AsT1,
        OneOf.OneOf<CompletionReport, ValidationErrors, SettingsMissing, PortError> r => r.AsT1,
        _ => throw new InvalidOperationException("Unexpected result type."),
    };
}
