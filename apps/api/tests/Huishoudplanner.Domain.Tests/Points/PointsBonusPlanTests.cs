using System.Globalization;
using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Tests.Points;

/// <summary>
/// The pure bonus step of the reconciliation (ADR-0012, step 4 of <c>reconcileNow</c>). Monday 2026-09-14 is the first day of cycle 0 (to 2026-10-11);
/// the amounts are 5 / 3 / 20 / 10.
/// </summary>
public sealed class PointsBonusPlanTests
{
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");
    private static readonly DateOnly Anchor = new(2026, 9, 14);
    private static readonly BonusScheduleRow Row = new(new DateOnly(2026, 9, 14), new BonusAmounts(5, 3, 20, 10));

    private const string P1 = "0000000000000000000000a1";
    private const string P2 = "0000000000000000000000a2";

    private static DateTimeOffset At(string day) => DayKeys.FromDayKey(DayKeys.Parse(day), Amsterdam);

    private static string Id(int n) => n.ToString("x24", CultureInfo.InvariantCulture);

    private static BonusSettings Settings(DateOnly? floor = null, params BonusScheduleRow[] rows) => new(Anchor, rows.Length == 0 ? [Row] : rows, floor);

    /// <summary>A planned occurrence, done on time on its day (at 10:00 local) unless said otherwise.</summary>
    private static BonusSource Done(int n, string person, string day, DateTimeOffset? completedAt = null) => new(
        Id(n), OccurrenceStatus.Done, At(day), At(day), false, person, false, null, person, completedAt ?? At(day).AddHours(10));

    private static BonusSource Open(int n, string? person, string day) => new(
        Id(n), OccurrenceStatus.Open, At(day), At(day), false, person, false, null, null, null);

    private static PointEntry Stored(BonusEntryInsert insert, int n = 1) => new(
        Id(7000 + n), insert.Key, insert.Kind, insert.PersonId, insert.Amount, insert.Date, insert.WeekStart, insert.PeriodStart,
        null, null, string.Empty, PointEntrySource.Recompute, null, null, null, insert.Date, insert.Date);

    private static List<PointEntry> Applied(IEnumerable<BonusEntryInsert> inserts) => [.. inserts.Select((insert, i) => Stored(insert, i + 1))];

    private static IReadOnlyList<BonusSource> TheWeek { get; } = [Done(1, P1, "2026-09-14"), Done(2, P2, "2026-09-15"), Open(3, null, "2026-09-16")];

    private static BonusPlan PlanFor(IReadOnlyList<BonusSource> sources, IReadOnlyList<PointEntry> stored, string today, BonusSettings? settings = null) =>
        PointsReconciliation.PlanBonuses(stored, sources, settings ?? Settings(), Amsterdam, DayKeys.Parse(today));

    [Fact]
    public void Plan_insertsTheFourWeekBonusesOfAnEndedWeekDatedOnItsLastDay()
    {
        var plan = PlanFor(TheWeek, [], "2026-09-21");

        plan.Changes.Deletes.Should().BeEmpty();
        plan.Changes.Inserts.Select(i => (i.Kind, i.PersonId, i.Amount)).Should().BeEquivalentTo(
        [
            (PointEntryKind.BonusWeekDone, P1, 5), (PointEntryKind.BonusWeekOnTime, P1, 3),
            (PointEntryKind.BonusWeekDone, P2, 5), (PointEntryKind.BonusWeekOnTime, P2, 3),
        ]);
        var first = plan.Changes.Inserts.Single(i => i.Kind == PointEntryKind.BonusWeekDone && i.PersonId == P1);
        first.Key.Should().Be($"bonus_week_done:{P1}:2026-09-14");
        (first.Date, first.WeekStart, first.PeriodStart).Should().Be((At("2026-09-20"), At("2026-09-14"), At("2026-09-14")));
        plan.SkippedIds.Should().BeEmpty();
    }

    [Fact]
    public void Plan_writesNothingWhileThePeriodHasNotEnded()
    {
        // Sunday the 20th is the last day of the week: it ends when Monday starts.
        PlanFor(TheWeek, [], "2026-09-20").Changes.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Plan_isIdempotent_planningAgainAfterTheResultWasAppliedGivesAnEmptyPlan()
    {
        var stored = Applied(PlanFor(TheWeek, [], "2026-10-12").Changes.Inserts);

        stored.Should().HaveCount(8, "four week bonuses and four cycle bonuses");
        PlanFor(TheWeek, stored, "2026-10-12").Changes.IsEmpty.Should().BeTrue();
        PlanFor(TheWeek, stored, "2026-10-13").Changes.IsEmpty.Should().BeTrue("a run on another day gives the same ledger");
    }

    [Fact]
    public void Plan_deletesABonusThatIsNoLongerExpectedAndRewritesOneWhoseAmountDrifted()
    {
        var expected = PlanFor(TheWeek, [], "2026-09-21").Changes.Inserts;
        BonusEntryInsert Of(PointEntryKind kind, string person) => expected.Single(i => i.Kind == kind && i.PersonId == person);
        var stored = new List<PointEntry>
        {
            Stored(Of(PointEntryKind.BonusWeekOnTime, P1), 1),
            Stored(Of(PointEntryKind.BonusWeekDone, P1), 2),
            Stored(Of(PointEntryKind.BonusWeekDone, P2) with { Amount = 99 }, 3),
        };
        // Person 1 undid their task: the set of person 1 is empty, so both of their bonuses go.
        var sources = new[] { Open(1, P1, "2026-09-14"), Done(2, P2, "2026-09-15") };

        var plan = PlanFor(sources, stored, "2026-09-22");

        plan.Changes.Deletes.Select(d => d.Key).Should().BeEquivalentTo(
            [$"bonus_week_ontime:{P1}:2026-09-14", $"bonus_week_done:{P1}:2026-09-14", $"bonus_week_done:{P2}:2026-09-14"]);
        // The drifted one is written again with the right amount; the on-time one of person 2 was missing.
        plan.Changes.Inserts.Select(i => (i.Key, i.Amount)).Should().BeEquivalentTo(
            [($"bonus_week_done:{P2}:2026-09-14", 5), ($"bonus_week_ontime:{P2}:2026-09-14", 3)]);
    }

    [Fact]
    public void Plan_paysNothingForSkippedWorkAndUnassignedOpenWorkBlocksNobody()
    {
        var sources = new[]
        {
            Done(1, P1, "2026-09-14"),
            new BonusSource(Id(2), OccurrenceStatus.Skipped, At("2026-09-15"), At("2026-09-15"), false, P2, false, null, null, null),
            Open(3, null, "2026-09-16"),
        };

        PlanFor(sources, [], "2026-09-21").Changes.Inserts.Should().HaveCount(2).And.OnlyContain(i => i.PersonId == P1);
    }

    [Fact]
    public void Plan_aLateCompletionPaysDoneButNotOnTime()
    {
        var late = Done(1, P1, "2026-09-14", At("2026-09-22").AddHours(8));

        PlanFor([late], [], "2026-09-23").Changes.Inserts.Select(i => i.Kind).Should().Equal(PointEntryKind.BonusWeekDone);
    }

    [Fact]
    public void Plan_aFrozenPeriodOwnerKeepsTheItemInTheirWeekAndTheTakerGainsNothingFromIt()
    {
        // Person 2 owns the item (frozen) of the ended week; person 1 took it over and finished it late.
        var takenOver = new BonusSource(Id(2), OccurrenceStatus.Done, At("2026-09-15"), At("2026-09-15"), false, P1, true, P2, P1, At("2026-09-23").AddHours(8));

        var plan = PlanFor([Done(1, P1, "2026-09-14"), takenOver], [], "2026-09-24");

        plan.Changes.Inserts.Select(i => (i.PersonId, i.Kind)).Should().Contain((P1, PointEntryKind.BonusWeekDone));
        plan.Changes.Inserts.Should().NotContain(i => i.PersonId == P2, "somebody else did the work of person 2");
    }

    [Fact]
    public void Plan_aBonusIsPaidWithTheAmountsInForceOnTheLastDayOfThePeriod()
    {
        var changed = new BonusScheduleRow(new DateOnly(2026, 9, 20), new BonusAmounts(50, 0, 0, 0));

        var inForce = PlanFor([Done(1, P1, "2026-09-14")], [], "2026-09-21", Settings(null, Row, changed));

        inForce.Changes.Inserts.Should().ContainSingle().Which.Amount.Should().Be(50);
        PlanFor([Done(1, P1, "2026-09-14")], [], "2026-09-21", new BonusSettings(Anchor, [], null)).Changes.IsEmpty.Should().BeTrue("amounts of 0 are the default");
    }

    [Fact]
    public void Plan_aPeriodThatStartedBeforeTheFloorIsNeverEvaluated_andItsStoredBonusIsRemoved()
    {
        var stored = Stored(PlanFor(TheWeek, [], "2026-09-21").Changes.Inserts[0]);

        var plan = PlanFor(TheWeek, [stored], "2026-09-22", Settings(new DateOnly(2026, 9, 21)));

        plan.Changes.Inserts.Should().BeEmpty();
        plan.Changes.Deletes.Should().ContainSingle();
    }

    [Fact]
    public void Plan_anAnchorThatMovedRedrawsTheCycleBonuses()
    {
        var stored = Applied(PlanFor(TheWeek, [], "2026-10-12").Changes.Inserts);

        var plan = PlanFor(TheWeek, stored, "2026-10-12", new BonusSettings(new DateOnly(2026, 9, 7), [Row], null));

        plan.Changes.Deletes.Should().HaveCount(4).And.OnlyContain(d => d.Key.Contains("_cycle_", StringComparison.Ordinal) && d.Key.EndsWith("2026-09-14", StringComparison.Ordinal));
        plan.Changes.Inserts.Should().HaveCount(4).And.OnlyContain(i => i.Key.EndsWith("2026-09-07", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_anUnreadableOccurrenceIsSkippedAndLeavesTheBonusesOfEveryPersonItCouldCountForAlone()
    {
        var all = Applied(PlanFor(TheWeek, [], "2026-09-21").Changes.Inserts);
        var unreadable = new BonusSource(Id(9), OccurrenceStatus.Open, null, At("2026-09-14"), false, P1, false, null, null, null);
        // Both would lose the bonuses (their tasks are undone); only person 2 can be evaluated.
        var sources = new[] { Open(1, P1, "2026-09-14"), Open(2, P2, "2026-09-15"), unreadable };

        var plan = PlanFor(sources, all, "2026-09-22");

        plan.SkippedIds.Should().BeEquivalentTo([Id(9)]);
        plan.Changes.Deletes.Should().HaveCount(2).And.OnlyContain(d => d.PersonId == P2);
    }

    [Fact]
    public void Plan_anUnreadableOccurrenceAlsoBlocksItsFrozenOwnerAndTheOneWhoDidIt()
    {
        var unreadable = new BonusSource(Id(9), null, At("2026-09-14"), At("2026-09-14"), false, P1, true, P2, null, null);

        var plan = PlanFor([unreadable, Done(1, P1, "2026-09-14"), Done(2, P2, "2026-09-15")], [], "2026-09-21");

        plan.Changes.IsEmpty.Should().BeTrue();
        unreadable.People.Should().BeEquivalentTo([P1, P2]);
    }

    [Fact]
    public void Describe_countsAndListsOnlyTheWritesThatHappened_removedFirstThenCreated_eachByKey()
    {
        var inserts = PlanFor(TheWeek, [], "2026-09-21").Changes.Inserts;
        var stale = new List<PointEntry> { Stored(inserts[0], 1), Stored(inserts[1], 2) };
        var plan = new BonusPlan(new BonusEntryChanges([.. inserts], stale), new HashSet<string>());
        var applied = new AppliedBonusChanges([inserts[3].Key, inserts[2].Key], [stale[1].Key]);

        var report = PointsReconciliation.Describe(plan, applied);

        (report.Created, report.Removed, report.Total, report.Truncated).Should().Be((2, 1, 3, false));
        report.Listed.Select(c => c.Change).Should().Equal("removed", "created", "created");
        report.Listed.Skip(1).Select(c => c.Key).Should().BeInAscendingOrder(StringComparer.Ordinal);
        report.Listed[0].Should().Be(new PointsBonusChange(stale[1].Key, stale[1].PersonId, stale[1].Amount, "removed"));
    }

    [Fact]
    public void Describe_cutsTheListAt100ButKeepsTheTotal()
    {
        var inserts = Enumerable.Range(0, 130)
            .Select(n => new BonusEntryInsert($"bonus_week_done:{P1}:{n:0000}", PointEntryKind.BonusWeekDone, P1, 5, At("2026-09-20"), At("2026-09-14"), At("2026-09-14")))
            .ToList();
        var plan = new BonusPlan(new BonusEntryChanges(inserts, []), new HashSet<string>());

        var report = PointsReconciliation.Describe(plan, new AppliedBonusChanges([.. inserts.Select(i => i.Key)], []));

        (report.Created, report.Listed.Count, report.Total, report.Truncated).Should().Be((130, 100, 130, true));
    }
}
