using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;

namespace Huishoudplanner.Domain.Tests.Points;

/// <summary>The pure part of the reconciliation (ADR-0011): the plan that makes the stored execution entries match the done occurrences.</summary>
public sealed class PointsReconciliationTests
{
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private const string P1 = "0000000000000000000000a1";
    private const string P2 = "0000000000000000000000a2";

    private static DateTimeOffset At(string day) => DayKeys.FromDayKey(DayKeys.Parse(day), Amsterdam);

    private static string Id(int n) => n.ToString("x24", System.Globalization.CultureInfo.InvariantCulture);

    private static ExecutionSource Done(int n, string person = P1, int points = 30, string day = "2026-09-14") =>
        new(Id(n), Id(900), At(day), OccurrenceStatus.Done, person, null, points, null, 30, "Taak");

    private static PointEntry Stored(int n, string person = P1, int amount = 30, string day = "2026-09-14", string? key = null)
    {
        var date = At(day);
        return new PointEntry(Id(5000 + n), key ?? ExecutionPoints.Key(Id(n)), PointEntryKind.Execution, person, amount, date, At("2026-09-14"), null, Id(n), Id(900), "Taak",
            PointEntrySource.Live, null, null, null, date, date);
    }

    [Fact]
    public void Plan_insertsAnEntryForEveryDoneOccurrenceThatHasNone()
    {
        var plan = PointsReconciliation.Plan([], [Done(1), Done(2, P2, 10)], Amsterdam);

        plan.Changes.Inserts.Select(i => (i.Key, i.Fields.PersonId, i.Fields.Amount)).Should().Equal(
            (ExecutionPoints.Key(Id(1)), P1, 30), (ExecutionPoints.Key(Id(2)), P2, 10));
        (plan.Changes.Updates, plan.Changes.Deletes, plan.Corrections).Should().BeEquivalentTo((Array.Empty<PointEntryUpdate>(), Array.Empty<PointEntry>(), Array.Empty<PointsCorrection>()));
    }

    [Fact]
    public void Plan_updatesAnEntryThatDiffersAndReportsTheCorrection()
    {
        var plan = PointsReconciliation.Plan([Stored(1, P2, 9)], [Done(1)], Amsterdam);

        plan.Changes.Updates.Should().ContainSingle().Which.Fields.Should().BeEquivalentTo(new { PersonId = P1, Amount = 30 });
        plan.Corrections.Should().Equal(new PointsCorrection(ExecutionPoints.Key(Id(1)), new PointsHolding(P2, 9), new PointsHolding(P1, 30)));
        (plan.Changes.Inserts, plan.Changes.Deletes).Should().BeEquivalentTo((Array.Empty<PointEntryInsert>(), Array.Empty<PointEntry>()));
    }

    [Fact]
    public void Plan_aDifferentDateTitleOrTaskIsADifference()
    {
        PointsReconciliation.Plan([Stored(1, day: "2026-09-15")], [Done(1)], Amsterdam).Changes.Updates.Should().ContainSingle();
        PointsReconciliation.Plan([Stored(1) with { TitleSnapshot = "Oud" }], [Done(1)], Amsterdam).Changes.Updates.Should().ContainSingle();
        PointsReconciliation.Plan([Stored(1) with { TaskId = null }], [Done(1)], Amsterdam).Changes.Updates.Should().ContainSingle();
        PointsReconciliation.Plan([Stored(1) with { WeekStart = At("2026-09-07") }], [Done(1)], Amsterdam).Changes.Updates.Should().ContainSingle();
    }

    [Fact]
    public void Plan_deletesAnEntryWithoutAnOccurrenceAndReportsItAsRemoved()
    {
        var plan = PointsReconciliation.Plan([Stored(7, P1, 4)], [], Amsterdam);

        plan.Changes.Deletes.Should().ContainSingle().Which.Key.Should().Be(ExecutionPoints.Key(Id(7)));
        plan.Corrections.Should().Equal(new PointsCorrection(ExecutionPoints.Key(Id(7)), new PointsHolding(P1, 4), null));
    }

    [Fact]
    public void Plan_aDoneOccurrenceThatNoLongerEarnsPointsLosesItsEntry()
    {
        var plan = PointsReconciliation.Plan([Stored(1)], [Done(1, points: 0)], Amsterdam);

        plan.Changes.Deletes.Should().ContainSingle();
        plan.Unattributed.Should().Be(0);
    }

    [Fact]
    public void Plan_anEntryThatAlreadyMatchesIsLeftAlone_soPlanningTwiceIsIdempotent()
    {
        var plan = PointsReconciliation.Plan([Stored(1), Stored(2, P2)], [Done(1), Done(2, P2)], Amsterdam);

        plan.Changes.IsEmpty.Should().BeTrue();
        plan.Corrections.Should().BeEmpty();
    }

    [Fact]
    public void Plan_countsWorkOfNobodyThatEarnedPointsButNotWorkOfZeroPoints()
    {
        var nobody = Done(1, person: null!) with { CompletedBy = null };
        var zero = Done(2, points: 0) with { CompletedBy = null };

        var plan = PointsReconciliation.Plan([], [nobody, zero], Amsterdam);

        plan.Unattributed.Should().Be(1);
        plan.Changes.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Plan_anUnreadableOccurrenceIsSkippedCountedOnceAndItsEntryKept()
    {
        var broken = Done(1) with { Date = null };

        var plan = PointsReconciliation.Plan([Stored(1)], [broken, Done(2)], Amsterdam);

        plan.SkippedIds.Should().BeEquivalentTo([Id(1)]);
        plan.Changes.Deletes.Should().BeEmpty();
        plan.Changes.Inserts.Should().ContainSingle().Which.Key.Should().Be(ExecutionPoints.Key(Id(2)));
    }

    [Fact]
    public void Plan_isDeterministic_insertsAndCorrectionsFollowTheKeyOrder()
    {
        var plan = PointsReconciliation.Plan([Stored(8, P1, 1), Stored(6, P1, 2)], [Done(5), Done(3)], Amsterdam);

        plan.Changes.Inserts.Select(i => i.Key).Should().Equal(ExecutionPoints.Key(Id(3)), ExecutionPoints.Key(Id(5)));
        plan.Corrections.Select(c => c.Key).Should().Equal(ExecutionPoints.Key(Id(6)), ExecutionPoints.Key(Id(8)));
    }

    [Fact]
    public void Plan_aStoredEntryWithADifferentKeyShapeForTheSameOccurrenceIsAnOrphanAndTheRightOneIsInserted()
    {
        var plan = PointsReconciliation.Plan([Stored(1, key: "execution:other")], [Done(1)], Amsterdam);

        (plan.Changes.Inserts.Count, plan.Changes.Deletes.Count).Should().Be((1, 1));
    }

    // ---- the limit of the listed corrections

    [Fact]
    public void LimitCorrections_keepsTheFirst100WithTheTotalAndATruncationFlag()
    {
        var corrections = Enumerable.Range(0, 101).Select(i => new PointsCorrection("k" + i, new PointsHolding(P1, i + 1), null)).ToList();

        var (listed, total, truncated) = PointsReconciliation.LimitCorrections(corrections);

        (listed.Count, total, truncated).Should().Be((100, 101, true));
        listed[0].Key.Should().Be("k0");
        listed[^1].Key.Should().Be("k99");
    }

    [Fact]
    public void LimitCorrections_aListOfExactly100IsNotTruncated()
    {
        var corrections = Enumerable.Range(0, 100).Select(i => new PointsCorrection("k" + i, new PointsHolding(P1, 1), null)).ToList();

        var (listed, total, truncated) = PointsReconciliation.LimitCorrections(corrections);

        (listed.Count, total, truncated).Should().Be((100, 100, false));
    }

    // ---- the result

    [Fact]
    public void Result_aRunThatChangedNothingIsNotChangedAnythingSoItIsNeverAudited()
    {
        PointsRecomputeResult.Empty(PointsRecomputeTrigger.Nightly).ChangedAnything.Should().BeFalse();
        (PointsRecomputeResult.Empty(PointsRecomputeTrigger.Nightly) with { Unattributed = 3, Skipped = 2 }).ChangedAnything.Should().BeFalse("only a write counts");
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 0)]
    [InlineData(0, 1, 0, 0, 0)]
    [InlineData(0, 0, 1, 0, 0)]
    [InlineData(0, 0, 0, 1, 0)]
    [InlineData(0, 0, 0, 0, 1)]
    public void Result_anyWriteCountsAsAChange(int tasks, int snapshots, int created, int updated, int removed) =>
        (PointsRecomputeResult.Empty(PointsRecomputeTrigger.Admin) with { TasksDefaulted = tasks, SnapshotsSet = snapshots, Created = created, Updated = updated, Removed = removed })
            .ChangedAnything.Should().BeTrue();
}
