using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Statistics;
using static Huishoudplanner.Domain.Tests.Statistics.StatisticsWorld;

namespace Huishoudplanner.Domain.Tests.Statistics;

/// <summary>The decisions of a statistics reset (<c>resetStatistics</c> in <c>domain/stats.ts</c>) and its audit entry.</summary>
public sealed class StatisticsResetTests
{
    private static readonly DateTimeOffset MondayMorning = Instant("2026-09-14T06:00:00Z");

    private static HouseholdSettings Settings(DateOnly? bonusFloor = null) =>
        SettingsDefaults.ForNewInstallation(Timezone, Anchor, MondayMorning) with { BonusFloor = bonusFloor };

    private static StatisticsResetPlan Plan(DateTimeOffset now, DateOnly? before = null, DateOnly? floor = null) =>
        StatisticsResetPlan.Create(Settings(floor), now, before).AsT0;

    [Fact]
    public void Create_withoutBefore_startsOverFromToday()
    {
        var plan = Plan(Instant("2026-09-16T08:00:00Z"));

        plan.RestartFromToday.Should().BeTrue();
        plan.BoundaryKey.Should().Be(Day("2026-09-16"));
        plan.Boundary.Should().Be(Instant("2026-09-15T22:00:00Z"), "midnight of 16 September in Amsterdam is 22:00 UTC the evening before");
        plan.BoundaryCycle.Should().Be(0);
        plan.BonusFloor.Should().Be(Day("2026-09-16"));
        plan.MovesBonusFloor.Should().BeTrue();
    }

    [Fact]
    public void Create_withBefore_purgesOnlyOlderData_andTheBoundaryCycleIsThatOfBefore()
    {
        var plan = Plan(Instant("2026-10-14T08:00:00Z"), Day("2026-10-12"));

        plan.RestartFromToday.Should().BeFalse();
        plan.BoundaryKey.Should().Be(Day("2026-10-12"));
        plan.BoundaryCycle.Should().Be(1);
    }

    [Fact]
    public void Create_withBeforeToday_isAllowed()
    {
        StatisticsResetPlan.Create(Settings(), Instant("2026-09-14T06:00:00Z"), Day("2026-09-14")).IsT0.Should().BeTrue();
    }

    [Fact]
    public void Create_withBeforeAfterToday_isRefused()
    {
        StatisticsResetPlan.Create(Settings(), Instant("2026-09-14T06:00:00Z"), Day("2026-09-15")).IsT1.Should().BeTrue();
    }

    [Fact]
    public void Create_judgesTodayInTheHouseholdTimezone()
    {
        // 22:30 UTC on 14 September is already 15 September in Amsterdam.
        StatisticsResetPlan.Create(Settings(), Instant("2026-09-14T22:30:00Z"), Day("2026-09-15")).IsT0.Should().BeTrue();
    }

    [Fact]
    public void Create_theBonusFloorOnlyMovesForward()
    {
        var later = Plan(Instant("2026-10-14T08:00:00Z"), Day("2026-10-01"), floor: Day("2026-10-12"));

        later.BonusFloor.Should().Be(Day("2026-10-12"));
        later.MovesBonusFloor.Should().BeFalse();
        Plan(Instant("2026-10-14T08:00:00Z"), Day("2026-10-12"), floor: Day("2026-10-01")).BonusFloor.Should().Be(Day("2026-10-12"));
    }

    [Fact]
    public void Create_aFloorOfTheSameDay_isNotWrittenAgain()
    {
        Plan(Instant("2026-10-14T08:00:00Z"), Day("2026-10-12"), floor: Day("2026-10-12")).MovesBonusFloor.Should().BeFalse();
    }

    [Fact]
    public void Audit_ofAStartOver_recordsTheCountsAndTheFloorOnTheSettings()
    {
        var plan = Plan(Instant("2026-09-16T08:00:00Z"), floor: Day("2026-09-01"));
        var result = new StatisticsResetResult(1, 2, 3, 4, 5, 6, 7);

        var entry = StatisticsResetAudit.ForReset(AuditActor.System, plan, result, "reset-id");

        entry.Entity.Should().Be(AuditEntity.Settings);
        entry.EntityId.Should().Be(SettingsIds.Singleton);
        entry.Action.Should().Be(AuditAction.Reset);
        entry.Before.Should().Be(AuditObject.Of(("statistics", "bestaande uitvoeringsgeschiedenis"), ("bonusFloor", "2026-09-01")));
        entry.After.Should().Be(AuditObject.Of(("statistics", "opnieuw gestart"), ("bonusFloor", "2026-09-16")));
        entry.Meta.Should().Be(AuditObject.Of(
            ("deletedOccurrences", 1),
            ("deletedRecorded", 2),
            ("resetOccurrences", 3),
            ("resetTasks", 4),
            ("deletedPastCycles", 5),
            ("removedPointEntries", 6),
            ("removedRedemptions", 7),
            ("resetId", "reset-id"),
            ("scoped", false)));
    }

    [Fact]
    public void Audit_ofAPurge_isScoped_andOmitsAFloorThatWasNotThere()
    {
        var plan = Plan(Instant("2026-10-14T08:00:00Z"), Day("2026-10-12"));

        var entry = StatisticsResetAudit.ForReset(AuditActor.System, plan, new StatisticsResetResult(4, 0, 0, 0, 1, 0, 0), "reset-id");

        entry.Before.Should().Be(AuditObject.Of(("statistics", "bestaande uitvoeringsgeschiedenis")));
        entry.After.Should().Be(AuditObject.Of(("statistics", "oude data opgeschoond"), ("bonusFloor", "2026-10-12")));
        entry.Meta!["scoped"].Should().Be(new AuditBool(true));
    }
}
