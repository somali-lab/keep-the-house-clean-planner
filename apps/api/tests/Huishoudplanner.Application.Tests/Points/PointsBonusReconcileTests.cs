using Huishoudplanner.Application.Tests.Occurrences;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Application.Tests.Points;

/// <summary>
/// Step 4 of the reconciliation (ADR-0012), through the real <see cref="PointsService"/> on in-memory ports: <c>points-bonuses.test.ts</c>.
/// Monday 2026-09-14 is the first day of cycle 0; the amounts are 5 / 3 / 20 / 10 from that day; the nightly run is on Monday 03:00 local time.
/// </summary>
public sealed class PointsBonusReconcileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Monday03 = new(2026, 9, 21, 1, 0, 0, TimeSpan.Zero);

    private static PointsWorld World(bool amounts = true)
    {
        var w = new PointsWorld();
        if (amounts)
        {
            w.Occ.SettingsStore.Document = w.Occ.SettingsStore.Document! with
            {
                BonusSchedule = [new BonusScheduleRow(new DateOnly(2026, 9, 14), new BonusAmounts(5, 3, 20, 10))],
            };
        }

        w.Occ.Clock.Now = Monday03;
        return w;
    }

    /// <summary>Person 1 and person 2 do their task on time in the week of 14 September; the unassigned one stays open.</summary>
    private static (Occurrence Monday, Occurrence Tuesday, Occurrence Wednesday) TheWeek(PointsWorld w) => (
        w.Done(w.Occ.Weekly, "2026-09-14", w.Occ.P1, w.Occ.P1),
        w.Done(w.Occ.Weekly, "2026-09-15", w.Occ.P2, w.Occ.P2),
        w.Occ.Seed(w.Occ.Weekly, "2026-09-16"));

    private static async Task<PointsRecomputeResult> Run(PointsWorld w, PointsRecomputeTrigger trigger = PointsRecomputeTrigger.Nightly) =>
        (await w.Service.RecomputeAsync(AuditActor.System, trigger, Ct)).AsT0;

    /// <summary>A readable view of the stored bonuses: kind, person, first day of the period and amount, in a stable order.</summary>
    private static List<string> Bonuses(PointsWorld w)
    {
        string Name(string id) => id == w.Occ.P1.Id ? "p1" : id == w.Occ.P2.Id ? "p2" : id;
        return [.. w.Ledger.Items
            .Where(e => e.Kind != PointEntryKind.Execution && e.Kind != PointEntryKind.Redemption)
            .Select(e => $"{PointNames.ToWire(e.Kind)} {Name(e.PersonId)} {Huishoudplanner.Domain.Calendar.DayKeys.ToDayKey(e.PeriodStart!.Value, OccurrenceWorld.Zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)} {e.Amount}")
            .Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Recompute_createsTheWeekBonusesWithOneSummaryAndASecondRunAndARestartWriteNothing()
    {
        var w = World();
        TheWeek(w);

        var result = await Run(w);

        result.Should().BeEquivalentTo(new { Created = 2, BonusesCreated = 4, BonusesRemoved = 0, BonusChangesTotal = 4, BonusChangesTruncated = false });
        Bonuses(w).Should().HaveCount(4);
        var summary = w.PointsAudit(AuditAction.Recompute).Should().ContainSingle().Subject;
        summary.Meta!["bonusesCreated"].Should().Be(new AuditInteger(4));
        summary.Meta!["bonusChangesTotal"].Should().Be(new AuditInteger(4));
        // The people inside the summary are hexadecimal strings, not object ids.
        var changes = ((AuditArray)summary.Meta!["bonusChanges"]!).Items.Cast<AuditObject>().ToList();
        changes.Should().HaveCount(4);
        changes.Should().Contain(c => Equals(c["key"], new AuditString($"bonus_week_done:{w.Occ.P1.Id}:2026-09-14")) &&
            Equals(c["personId"], new AuditString(w.Occ.P1.Id)) && Equals(c["amount"], new AuditInteger(5)) && Equals(c["change"], new AuditString("created")));

        var entry = w.Ledger.Items.Single(e => e.Kind == PointEntryKind.BonusWeekDone && e.PersonId == w.Occ.P1.Id);
        (entry.OccurrenceId, entry.TaskId, entry.TitleSnapshot, entry.Source).Should().Be((null, null, string.Empty, PointEntrySource.Recompute));
        // Dated on the last day of the period, so a balance range that includes that day includes the bonus.
        (entry.Date, entry.WeekStart, entry.PeriodStart).Should().Be((OccurrenceWorld.At("2026-09-20"), OccurrenceWorld.At("2026-09-14"), OccurrenceWorld.At("2026-09-14")));

        var (writes, audits) = (w.Ledger.Writes, w.Occ.Audit.Entries.Count);
        var again = await Run(w, PointsRecomputeTrigger.Startup);
        again.Should().BeEquivalentTo(new { Created = 0, BonusesCreated = 0, BonusesRemoved = 0, BonusChangesTotal = 0 });
        (w.Ledger.Writes, w.Occ.Audit.Entries.Count).Should().Be((writes, audits));
    }

    [Fact]
    public async Task Recompute_writesNothingWhileTheWeekHasNotEnded_SundayTwentyThreeHundredLocal()
    {
        var w = World();
        TheWeek(w);
        w.Occ.Clock.Now = new DateTimeOffset(2026, 9, 20, 21, 0, 0, TimeSpan.Zero);

        var result = await Run(w);

        result.Should().BeEquivalentTo(new { BonusesCreated = 0 });
        Bonuses(w).Should().BeEmpty();
    }

    [Fact]
    public async Task Recompute_doesNothingWhileTheAmountsAreZero_whichIsTheDefault()
    {
        var w = World(amounts: false);
        TheWeek(w);
        // Execution entries are written; the bonuses are not, and a run without a bonus change adds no bonus fields to the summary beyond zero.

        var result = await Run(w);

        result.Should().BeEquivalentTo(new { BonusesCreated = 0, BonusesRemoved = 0, BonusChangesTotal = 0 });
        Bonuses(w).Should().BeEmpty();
    }

    [Fact]
    public async Task Recompute_paysNothingForSkippedWorkAndUnassignedOpenWorkBlocksNobody()
    {
        var w = World();
        w.Done(w.Occ.Weekly, "2026-09-14", w.Occ.P1, w.Occ.P1);
        w.Occ.Seed(w.Occ.Weekly, "2026-09-15", w.Occ.P2, OccurrenceStatus.Skipped);
        w.Occ.Seed(w.Occ.Weekly, "2026-09-16");

        await Run(w);

        Bonuses(w).Should().Equal("bonus_week_done p1 2026-09-14 5", "bonus_week_ontime p1 2026-09-14 3");
    }

    [Fact]
    public async Task Recompute_anUncompleteRemovesTheBonusesAndALateCheckOffAddsOnlyDone()
    {
        var w = World();
        var (monday, _, _) = TheWeek(w);
        await Run(w);
        Bonuses(w).Should().HaveCount(4);

        // Check-offs and corrections never write a bonus: the ledger follows at the next run.
        w.Occ.Occurrences.Items[w.Occ.Occurrences.Items.FindIndex(o => o.Id == monday.Id)] = monday with { Status = OccurrenceStatus.Open, CompletedAt = null, CompletedBy = null };
        Bonuses(w).Should().HaveCount(4);
        w.Occ.Clock.Now = new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);
        var removed = await Run(w, PointsRecomputeTrigger.Admin);
        removed.Should().BeEquivalentTo(new { BonusesCreated = 0, BonusesRemoved = 2, BonusChangesTotal = 2 });
        removed.BonusChanges.Select(c => (c.Change, c.PersonId, c.Amount)).Should().Equal(
            ("removed", w.Occ.P1.Id, 5), ("removed", w.Occ.P1.Id, 3));
        Bonuses(w).Should().HaveCount(2).And.OnlyContain(b => b.Contains(" p2 ", StringComparison.Ordinal));

        // Completed again, after the end of the week: done, but not on time.
        w.Occ.Occurrences.Items[w.Occ.Occurrences.Items.FindIndex(o => o.Id == monday.Id)] =
            monday with { Status = OccurrenceStatus.Done, CompletedAt = new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero), CompletedBy = w.Occ.P1.Id };
        w.Occ.Clock.Now = new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);
        var late = await Run(w);
        late.Should().BeEquivalentTo(new { BonusesCreated = 1, BonusesRemoved = 0 });
        Bonuses(w).Should().Contain("bonus_week_done p1 2026-09-14 5").And.NotContain("bonus_week_ontime p1 2026-09-14 3");
        w.PointsAudit(AuditAction.Recompute).Should().HaveCount(3);
    }

    [Fact]
    public async Task Recompute_aFrozenPeriodOwnerKeepsTheFinalisedBonusWhenSomebodyTakesTheItemOver()
    {
        var w = World();
        var (_, tuesday, _) = TheWeek(w);
        await Run(w);
        // Person 2 undid Tuesday after the week ended; person 1 took it over (the owner was frozen as person 2) and finished it late.
        var index = w.Occ.Occurrences.Items.FindIndex(o => o.Id == tuesday.Id);
        w.Occ.Occurrences.Items[index] = tuesday with
        {
            AssigneeId = w.Occ.P1.Id,
            PeriodOwnerId = w.Occ.P2.Id,
            PeriodOwnerFrozen = true,
            CompletedBy = w.Occ.P1.Id,
            CompletedAt = new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero),
        };
        w.Occ.Clock.Now = new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

        var result = await Run(w);

        // Person 2 still owns the item and did not do it; person 1 did it for somebody else, which neither blocks nor pays.
        result.Should().BeEquivalentTo(new { BonusesCreated = 0, BonusesRemoved = 2 });
        Bonuses(w).Should().HaveCount(2).And.OnlyContain(b => b.Contains(" p1 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recompute_anAnchorThatMovedRedrawsTheCycleBonuses()
    {
        var w = World();
        TheWeek(w);
        w.Occ.Clock.Now = new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);
        await Run(w);
        Bonuses(w).Should().HaveCount(8);

        w.Occ.SettingsStore.Document = w.Occ.SettingsStore.Document! with { CycleAnchorDate = new DateOnly(2026, 9, 7) };
        var result = await Run(w);

        result.Should().BeEquivalentTo(new { BonusesCreated = 4, BonusesRemoved = 4, BonusChangesTotal = 8 });
        result.BonusChanges.Take(4).Should().OnlyContain(c => c.Change == "removed" && c.Key.EndsWith("2026-09-14", StringComparison.Ordinal));
        result.BonusChanges.Skip(4).Should().OnlyContain(c => c.Change == "created" && c.Key.EndsWith("2026-09-07", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recompute_theFloorKeepsAPurgedWeekFromPayingOutOnWhatRemains_andRemovingItAwardsItAgain()
    {
        var w = World();
        w.Occ.SettingsStore.Document = w.Occ.SettingsStore.Document! with { BonusFloor = new DateOnly(2026, 9, 21) };
        // Only an item that was dragged out of the purged week survived, and it was finished the week after.
        w.Occ.Seed(w.Occ.Weekly, "2026-09-14", w.Occ.P1, OccurrenceStatus.Done, o => o with
        {
            StatusBeforeCompletion = OccurrenceStatus.Open,
            Date = OccurrenceWorld.At("2026-09-22"),
            CompletedAt = OccurrenceWorld.At("2026-09-22").AddHours(10),
            CompletedBy = w.Occ.P1.Id,
            PointsSnapshot = 30,
        });
        w.Occ.Clock.Now = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

        await Run(w);
        Bonuses(w).Should().BeEmpty();

        w.Occ.SettingsStore.Document = w.Occ.SettingsStore.Document! with { BonusFloor = null };
        await Run(w);
        Bonuses(w).Should().Equal("bonus_week_done p1 2026-09-14 5");
    }

    [Fact]
    public async Task Recompute_anOccurrenceThatCannotBeReadLeavesTheBonusesOfItsPeopleAloneAndCountsItAsSkippedOnce()
    {
        var w = World();
        var (monday, tuesday, _) = TheWeek(w);
        await Run(w);
        var broken = w.Occ.Seed(w.Occ.Weekly, "2026-09-14", w.Occ.P1);
        w.Backfill.UnreadableBonusIds.Add(broken.Id);
        w.Backfill.UnreadableIds.Add(monday.Id);
        // Both people would lose the bonuses (their tasks are undone); only person 2 can be evaluated.
        foreach (var done in new[] { monday, tuesday })
        {
            w.Occ.Occurrences.Items[w.Occ.Occurrences.Items.FindIndex(o => o.Id == done.Id)] = done with { Status = OccurrenceStatus.Open, CompletedAt = null, CompletedBy = null };
        }

        w.Occ.Clock.Now = new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);
        var result = await Run(w);

        result.Skipped.Should().Be(1, "the occurrence that cannot be read for the bonuses is counted once");
        Bonuses(w).Should().HaveCount(2).And.OnlyContain(b => b.Contains(" p1 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recompute_aBonusDeleteThatMissedItsCompareAndSetIsNotCounted()
    {
        var w = World();
        var (monday, _, _) = TheWeek(w);
        await Run(w);
        w.Occ.Occurrences.Items[w.Occ.Occurrences.Items.FindIndex(o => o.Id == monday.Id)] = monday with { Status = OccurrenceStatus.Open, CompletedAt = null, CompletedBy = null };
        // Somebody changed one of the entries after the reconciliation read it: its compare-and-set misses.
        w.Ledger.ConcurrentWriteBeforeNextBonusWrite = () =>
        {
            var index = w.Ledger.Items.FindIndex(e => e.Kind == PointEntryKind.BonusWeekDone && e.PersonId == w.Occ.P1.Id);
            w.Ledger.Items[index] = w.Ledger.Items[index] with { Amount = 99 };
        };
        w.Occ.Clock.Now = new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

        var result = await Run(w);

        result.Should().BeEquivalentTo(new { BonusesRemoved = 1, BonusChangesTotal = 1 });
        w.Ledger.Items.Should().Contain(e => e.Kind == PointEntryKind.BonusWeekDone && e.PersonId == w.Occ.P1.Id && e.Amount == 99);
    }

    [Fact]
    public async Task Recompute_aCycleAnchorThatIsNoMondayRollsTheWholeRunBack()
    {
        var w = World();
        TheWeek(w);
        w.Occ.SettingsStore.Document = w.Occ.SettingsStore.Document! with { CycleAnchorDate = new DateOnly(2026, 9, 15) };
        w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);

        var failed = await w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Nightly, Ct);

        failed.IsT2.Should().BeTrue();
        failed.AsT2.Message.Should().StartWith("points.bonus_anchor_invalid");
        w.Ledger.Items.Should().BeEmpty("the execution entries of the same run are rolled back with the bonuses");
        w.PointsAudit().Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_neverWritesOrTouchesABonus()
    {
        var w = World();
        var (monday, _, _) = TheWeek(w);
        await Run(w);
        var before = Bonuses(w);
        var bonusWrites = w.Ledger.BonusBulkWrites;

        w.Occ.Occurrences.Items[w.Occ.Occurrences.Items.FindIndex(o => o.Id == monday.Id)] = monday with { Status = OccurrenceStatus.Open, CompletedAt = null, CompletedBy = null };
        (await w.Service.SyncAsync(PointsWorld.Actor(w.Occ.P1), monday.Id, PointsSyncReason.Uncomplete, Ct)).AsT0.Should().Be(Huishoudplanner.Domain.Ports.Driving.SyncOutcome.Deleted);

        Bonuses(w).Should().Equal(before);
        w.Ledger.BonusBulkWrites.Should().Be(bonusWrites);
        w.PointsAudit(AuditAction.Recompute).Should().ContainSingle();
    }

    [Fact]
    public async Task Recompute_aFailingBonusReadIsAPortErrorAndWritesNothing()
    {
        var w = World();
        TheWeek(w);
        w.Backfill.Failure = new Huishoudplanner.Domain.Errors.PortError("occurrences.down");

        var failed = await w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Nightly, Ct);

        failed.IsT2.Should().BeTrue();
        w.Ledger.Items.Should().BeEmpty();
        w.PointsAudit().Should().BeEmpty();
    }
}
