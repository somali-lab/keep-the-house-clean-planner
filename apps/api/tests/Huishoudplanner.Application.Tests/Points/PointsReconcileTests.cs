using Huishoudplanner.Application.Tests.Occurrences;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;

namespace Huishoudplanner.Application.Tests.Points;

/// <summary>
/// The reconciliation (ADR-0011): <c>points-reconcile.test.ts</c>. Monday 2026-09-14 is the first day of cycle 0; the task is 30 minutes (30 points
/// by default). The bonus step is in <c>PointsBonusReconcileTests</c>; the badge step (4.5), the statistics reset (5.1) and the import (7.x) are not here.
/// </summary>
public sealed class PointsReconcileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Key(string occurrenceId) => "execution:" + occurrenceId;

    private static async Task<PointsRecomputeResult> Run(PointsWorld w, PointsRecomputeTrigger trigger = PointsRecomputeTrigger.Admin) =>
        (await w.Service.RecomputeAsync(AuditActor.System, trigger, Ct)).AsT0;

    private static AuditEntry Summary(PointsWorld w) => w.PointsAudit(AuditAction.Recompute).Single();

    /// <summary>Old data: done without a snapshot and without a ledger entry.</summary>
    private static Occurrence Legacy(PointsWorld w, string day, Huishoudplanner.Domain.Users.User? assignee, Huishoudplanner.Domain.Users.User? completedBy, Func<Occurrence, Occurrence>? tweak = null) =>
        w.Occ.Seed(w.Occ.Weekly, day, assignee, OccurrenceStatus.Done, o => (tweak?.Invoke(o) ?? o) with
        {
            StatusBeforeCompletion = OccurrenceStatus.Open,
            CompletedAt = OccurrenceWorld.At(day).AddHours(10),
            CompletedBy = completedBy?.Id,
            PointsSnapshot = null,
        });

    // ---- retroactive points

    [Fact]
    public async Task Recompute_awardsHistoricalExecutionsPerTheAdrWritesOneSummaryAndASecondRunWritesNothing()
    {
        var w = new PointsWorld();
        var (p1, p2) = (w.Occ.P1, w.Occ.P2);
        // Done with completedBy: credited to that person, not the assignee. Done without: the assignee. Done without anybody: unattributed.
        var monday = Legacy(w, "2026-09-14", p1, p2);
        var tuesday = Legacy(w, "2026-09-15", p2, null);
        var wednesday = Legacy(w, "2026-09-16", null, null);
        // The task of this occurrence no longer exists, and a one-off task has none: the duration rule applies.
        var orphanTask = Legacy(w, "2026-09-14", null, p1, o => o with { Id = w.Occ.Occurrences.NextId(), TaskId = "0000000000000000000000ee", DurationMinutesSnapshot = 25, Origin = OccurrenceOrigin.Adhoc });
        var oneOff = Legacy(w, "2026-09-14", p2, null, o => o with { Id = w.Occ.Occurrences.NextId(), TaskId = null, DurationMinutesSnapshot = 95, TaskNameSnapshot = "Zolder vegen", Origin = OccurrenceOrigin.Adhoc });
        // A task from before points existed.
        w.Backfill.Tasks[0] = w.Backfill.Tasks[0] with { Points = null };

        var result = await Run(w, PointsRecomputeTrigger.Startup);

        result.Should().BeEquivalentTo(new
        {
            Trigger = PointsRecomputeTrigger.Startup,
            TasksDefaulted = 1,
            SnapshotsSet = 5,
            Created = 4,
            Updated = 0,
            Removed = 0,
            Unattributed = 1,
            Skipped = 0,
            CorrectionsTotal = 0,
        });
        EntryOf(w, monday).Should().BeEquivalentTo(new { PersonId = p2.Id, Amount = 30, Source = PointEntrySource.Backfill, TitleSnapshot = "Badkamer schoonmaken", Date = OccurrenceWorld.At("2026-09-14") });
        EntryOf(w, tuesday).Should().BeEquivalentTo(new { PersonId = p2.Id, Amount = 30 });
        EntryOf(w, wednesday).Should().BeNull();
        EntryOf(w, orphanTask).Should().BeEquivalentTo(new { PersonId = p1.Id, Amount = 25 });
        EntryOf(w, oneOff).Should().BeEquivalentTo(new { PersonId = p2.Id, Amount = 95, TaskId = (string?)null, TitleSnapshot = "Zolder vegen" });
        w.Occ.Stored(wednesday).PointsSnapshot.Should().Be(30);
        w.Backfill.Tasks[0].Points.Should().Be(30);

        // One summary for all of it; no entry per ledger entry.
        var summary = Summary(w);
        (summary.Actor, summary.EntityId).Should().Be((AuditActor.System, PointsAudit.LedgerId));
        summary.Meta!["trigger"].Should().Be(new AuditString("startup"));
        summary.Meta["created"].Should().Be(new AuditInteger(4));
        summary.Meta["snapshotsSet"].Should().Be(new AuditInteger(5));
        summary.Meta["tasksDefaulted"].Should().Be(new AuditInteger(1));
        summary.Meta["unattributed"].Should().Be(new AuditInteger(1));
        w.PointsAudit().Should().ContainSingle();

        var (writes, audits) = (w.Ledger.Writes, w.Occ.Audit.Entries.Count);
        var again = await Run(w, PointsRecomputeTrigger.Nightly);
        again.Should().BeEquivalentTo(new { TasksDefaulted = 0, SnapshotsSet = 0, Created = 0, Updated = 0, Removed = 0 });
        (w.Ledger.Writes, w.Occ.Audit.Entries.Count, w.Backfill.SnapshotWrites).Should().Be((writes, audits, 5));
        w.Ledger.Items.Should().HaveCount(4);
    }

    private static PointEntry? EntryOf(PointsWorld w, Occurrence occurrence) => w.Ledger.Items.SingleOrDefault(e => e.Key == Key(occurrence.Id));

    [Fact]
    public async Task Recompute_neverRewritesASnapshotFromATaskValueThatChangedAfterwards()
    {
        var w = new PointsWorld();
        var monday = w.Done(w.Occ.Weekly, "2026-09-14", w.Occ.P1, w.Occ.P1);
        await w.Service.SyncAsync(PointsWorld.Actor(w.Occ.P1), monday.Id, PointsSyncReason.Complete, Ct);
        w.Backfill.Tasks[0] = w.Backfill.Tasks[0] with { Points = 9 };

        var result = await Run(w);

        result.Should().BeEquivalentTo(new { Created = 0, Updated = 0, Removed = 0, SnapshotsSet = 0 });
        EntryOf(w, monday)!.Amount.Should().Be(30);
    }

    [Fact]
    public async Task Recompute_healsDriftAndListsEveryCorrection()
    {
        var w = new PointsWorld();
        var (p1, p2) = (w.Occ.P1, w.Occ.P2);
        var monday = w.Done(w.Occ.Weekly, "2026-09-14", p1, p1);
        var tuesday = w.Done(w.Occ.Weekly, "2026-09-15", p2, p2);
        var wednesday = w.Done(w.Occ.Weekly, "2026-09-16", null, p1);
        foreach (var occurrence in new[] { monday, tuesday, wednesday })
        {
            await w.Service.SyncAsync(PointsWorld.Actor(p1), occurrence.Id, PointsSyncReason.Complete, Ct);
        }

        // Drift: a wrong person and amount, a lost entry, and an entry without an occurrence.
        var mondayEntry = EntryOf(w, monday)!;
        w.Ledger.Items[w.Ledger.Items.IndexOf(mondayEntry)] = mondayEntry with { PersonId = p2.Id, Amount = 9 };
        w.Ledger.Items.Remove(EntryOf(w, tuesday)!);
        var stray = Key("0000000000000000000000f1");
        w.Ledger.Items.Add(mondayEntry with { Id = w.Ledger.NextId(), Key = stray, OccurrenceId = "0000000000000000000000f1", PersonId = p1.Id, Amount = 4 });
        var auditBefore = w.Occ.Audit.Entries.Count;

        var result = await Run(w, PointsRecomputeTrigger.Nightly);

        result.Should().BeEquivalentTo(new { Trigger = PointsRecomputeTrigger.Nightly, Created = 1, Updated = 1, Removed = 1, SnapshotsSet = 0, TasksDefaulted = 0, CorrectionsTotal = 2, CorrectionsTruncated = false });
        result.Corrections.Should().BeEquivalentTo(
            [
                new PointsCorrection(Key(monday.Id), new PointsHolding(p2.Id, 9), new PointsHolding(p1.Id, 30)),
                new PointsCorrection(stray, new PointsHolding(p1.Id, 4), null),
            ],
            options => options.WithStrictOrdering());
        w.Ledger.Items.Select(e => (e.Key, e.PersonId, e.Amount)).Should().BeEquivalentTo(
        [
            (Key(monday.Id), p1.Id, 30), (Key(tuesday.Id), p2.Id, 30), (Key(wednesday.Id), p1.Id, 30),
        ]);
        EntryOf(w, monday)!.Source.Should().Be(PointEntrySource.Recompute);
        EntryOf(w, tuesday)!.Source.Should().Be(PointEntrySource.Backfill);
        w.Occ.Audit.Entries.Count.Should().Be(auditBefore + 1);
        var meta = Summary(w).Meta!;
        meta["correctionsTotal"].Should().Be(new AuditInteger(2));
        ((AuditArray)meta["corrections"]!).Items.Should().HaveCount(2);

        var again = await Run(w);
        again.Should().BeEquivalentTo(new { Created = 0, Updated = 0, Removed = 0 });
        w.Occ.Audit.Entries.Count.Should().Be(auditBefore + 1);
    }

    [Fact]
    public async Task Recompute_withoutSettingsThereIsNothingToReconcile()
    {
        var w = new PointsWorld();
        Legacy(w, "2026-09-14", w.Occ.P1, w.Occ.P1);
        w.Occ.SettingsStore.Document = null;

        var result = await Run(w);

        result.Should().Be(PointsRecomputeResult.Empty(PointsRecomputeTrigger.Admin));
        (w.Ledger.Writes, w.Occ.Audit.Entries.Count, w.Backfill.SnapshotWrites).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task Recompute_attributesTheSummaryToTheActorThatStartedIt()
    {
        var w = new PointsWorld();
        Legacy(w, "2026-09-14", w.Occ.P1, w.Occ.P1);

        var result = await w.Service.RecomputeAsync(PointsWorld.Actor(w.Occ.Admin), PointsRecomputeTrigger.Admin, Ct);

        result.AsT0.Created.Should().Be(1);
        var summary = Summary(w);
        summary.Actor.ActorId.Should().Be(w.Occ.Admin.Id);
        summary.Meta!["trigger"].Should().Be(new AuditString("admin"));
    }

    // ---- the snapshot of work from before snapshots

    [Fact]
    public async Task Recompute_aOneOffTaskWithoutSnapshotGetsThePointsItWasRecordedWithNotTheDurationRule()
    {
        var w = new PointsWorld();
        Occurrence OneOff(Func<Occurrence, Occurrence> tweak) =>
            Legacy(w, "2026-09-14", null, w.Occ.P1, o => tweak(o) with { Id = w.Occ.Occurrences.NextId(), TaskId = null, DurationMinutesSnapshot = 40, Origin = OccurrenceOrigin.Adhoc });
        var withPoints = OneOff(o => o with { PointsOverride = 7 });
        var noPoints = OneOff(o => o with { PointsOverride = 0 });
        var byDuration = OneOff(o => o);

        await Run(w);

        (w.Occ.Stored(withPoints).PointsSnapshot, w.Occ.Stored(noPoints).PointsSnapshot, w.Occ.Stored(byDuration).PointsSnapshot).Should().Be((7, 0, 40));
        EntryOf(w, withPoints).Should().BeEquivalentTo(new { Amount = 7, TaskId = (string?)null });
        EntryOf(w, noPoints).Should().BeNull();
        EntryOf(w, byDuration)!.Amount.Should().Be(40);
    }

    [Fact]
    public async Task Recompute_historicalExecutionsGetTheDefaultForTheDurationTheyHadNotTheTasksCurrentDuration()
    {
        var w = new PointsWorld();
        // The occurrence was made for 15 minutes; the task has since become a 60-minute task and has no points yet.
        var monday = Legacy(w, "2026-09-14", null, w.Occ.P1, o => o with { DurationMinutesSnapshot = 15 });
        w.Backfill.Tasks[0] = new TaskPointValue(w.Occ.Weekly.Id, null, 60);

        var result = await Run(w, PointsRecomputeTrigger.Startup);

        result.Should().BeEquivalentTo(new { TasksDefaulted = 1, SnapshotsSet = 1, Created = 1 });
        w.Backfill.Tasks[0].Points.Should().Be(60);
        w.Occ.Stored(monday).PointsSnapshot.Should().Be(15);
        EntryOf(w, monday)!.Amount.Should().Be(15);
    }

    [Fact]
    public async Task Recompute_aTaskWithAnExplicitValueStillGivesItsPoints()
    {
        var w = new PointsWorld();
        var monday = Legacy(w, "2026-09-14", null, w.Occ.P1, o => o with { DurationMinutesSnapshot = 15 });
        w.Backfill.Tasks[0] = w.Backfill.Tasks[0] with { Points = 9 };

        await Run(w, PointsRecomputeTrigger.Startup);

        w.Occ.Stored(monday).PointsSnapshot.Should().Be(9);
    }

    // ---- robustness

    [Fact]
    public async Task Recompute_skipsAndCountsAnUnreadableOccurrenceAndKeepsItsEntry()
    {
        var w = new PointsWorld();
        var monday = Legacy(w, "2026-09-14", null, w.Occ.P1);
        var broken = w.Done(w.Occ.Weekly, "2026-09-15", null, w.Occ.P1, snapshot: 2);
        w.Backfill.UnreadableIds.Add(broken.Id);
        var kept = new PointEntry(w.Ledger.NextId(), Key(broken.Id), PointEntryKind.Execution, w.Occ.P1.Id, 2, OccurrenceWorld.At("2026-09-14"), OccurrenceWorld.At("2026-09-14"),
            null, broken.Id, null, "Oud", PointEntrySource.Live, null, null, null, OccurrenceWorld.Now, OccurrenceWorld.Now);
        w.Ledger.Items.Add(kept);
        var tuesday = Legacy(w, "2026-09-16", null, w.Occ.P1);

        var result = await Run(w, PointsRecomputeTrigger.Startup);

        result.Should().BeEquivalentTo(new { Skipped = 1, Created = 2, Removed = 0, SnapshotsSet = 2 });
        w.Ledger.Items.Should().Contain(kept);
        EntryOf(w, monday).Should().NotBeNull();
        EntryOf(w, tuesday).Should().NotBeNull();
        Summary(w).Meta!["skipped"].Should().Be(new AuditInteger(1));
    }

    [Fact]
    public async Task Recompute_anEntryThatCannotBeReadIsLeftAloneAndCountedInsteadOfInsertedAgainOnItsKey()
    {
        var w = new PointsWorld();
        var broken = Legacy(w, "2026-09-14", null, w.Occ.P1);
        w.Ledger.UnreadableKeys.Add(Key(broken.Id));
        w.Ledger.UnreadableWithoutKey = 2;

        var result = await Run(w);

        result.Should().BeEquivalentTo(new { Skipped = 3, Created = 0, Removed = 0 });
        w.Ledger.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Recompute_listsAtMost100CorrectionsWithTheTotalAndATruncationFlag()
    {
        var w = new PointsWorld();
        for (var i = 0; i < 101; i++)
        {
            var id = (0xf0000 + i).ToString("x24", System.Globalization.CultureInfo.InvariantCulture);
            w.Ledger.Items.Add(new PointEntry(w.Ledger.NextId(), Key(id), PointEntryKind.Execution, w.Occ.P1.Id, i + 1, OccurrenceWorld.At("2026-09-14"), OccurrenceWorld.At("2026-09-14"),
                null, id, null, "Verdwaald", PointEntrySource.Live, null, null, null, OccurrenceWorld.Now, OccurrenceWorld.Now));
        }

        var result = await Run(w);

        result.Should().BeEquivalentTo(new { Removed = 101, CorrectionsTotal = 101, CorrectionsTruncated = true });
        result.Corrections.Should().HaveCount(100);
        var meta = Summary(w).Meta!;
        ((AuditArray)meta["corrections"]!).Items.Should().HaveCount(100);
        meta["correctionsTotal"].Should().Be(new AuditInteger(101));
        meta["correctionsTruncated"].Should().Be(new AuditBool(true));
    }

    [Fact]
    public async Task Recompute_leavesAnEntryAloneThatAnotherWriterChangedAfterItWasRead()
    {
        var w = new PointsWorld();
        var (p1, p2) = (w.Occ.P1, w.Occ.P2);
        var monday = w.Done(w.Occ.Weekly, "2026-09-14", p1, p1);
        var tuesday = w.Done(w.Occ.Weekly, "2026-09-15", p2, p2);
        foreach (var occurrence in new[] { monday, tuesday })
        {
            await w.Service.SyncAsync(PointsWorld.Actor(p1), occurrence.Id, PointsSyncReason.Complete, Ct);
        }

        // Drift that the reconciliation sees ...
        w.Ledger.Items[w.Ledger.Items.IndexOf(EntryOf(w, monday)!)] = EntryOf(w, monday)! with { Amount = 5 };
        w.Ledger.Items.Add(EntryOf(w, tuesday)! with { Id = w.Ledger.NextId(), Key = Key("0000000000000000000000f1"), OccurrenceId = "0000000000000000000000f1" });
        // ... and a live sync that moves both entries after they were read.
        w.Ledger.ConcurrentWriteBeforeNextBulk = () =>
        {
            w.Ledger.Items[w.Ledger.Items.IndexOf(EntryOf(w, monday)!)] = EntryOf(w, monday)! with { Amount = 7 };
            var orphan = w.Ledger.Items.Single(e => e.Key == Key("0000000000000000000000f1"));
            w.Ledger.Items[w.Ledger.Items.IndexOf(orphan)] = orphan with { PersonId = p1.Id };
        };

        var result = await Run(w);

        // Nothing was written, so nothing is counted: the next run sees the entries again.
        result.Should().BeEquivalentTo(new { Created = 0, Updated = 0, Removed = 0 });
        EntryOf(w, monday)!.Amount.Should().Be(7);
        w.Ledger.Items.Should().Contain(e => e.Key == Key("0000000000000000000000f1"));
        w.PointsAudit(AuditAction.Recompute).Should().BeEmpty();
    }

    [Fact]
    public async Task Recompute_aFailingStoreIsAPortErrorAndWritesNothing()
    {
        var w = new PointsWorld();
        Legacy(w, "2026-09-14", w.Occ.P1, w.Occ.P1);
        w.Ledger.WriteFailure = new PortError("pointEntries.failed: boom");

        var result = await w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Startup, Ct);

        result.AsT2.Message.Should().StartWith("pointEntries.failed");
        w.Transactions.Aborts.Should().Be(1);
        w.Occ.Stored(w.Occ.Occurrences.Items.Single()).PointsSnapshot.Should().BeNull("the snapshot backfill rolls back with the failed run");
        w.Occ.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Recompute_aFailingSummaryRollsTheWholeRunBack()
    {
        var w = new PointsWorld();
        Legacy(w, "2026-09-14", w.Occ.P1, w.Occ.P1);
        w.Occ.Audit.Failure = new PortError("audit.failed: boom");

        var result = await w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Startup, Ct);

        result.IsT2.Should().BeTrue();
        w.Ledger.Items.Should().BeEmpty();
        w.Occ.Occurrences.Items.Single().PointsSnapshot.Should().BeNull();
    }

    [Fact]
    public async Task Recompute_aConcurrentWriterThatKeepsWinningIsAConflict()
    {
        var w = new PointsWorld();
        w.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "lost");

        var result = await w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Admin, Ct);

        result.AsT1.Code.Should().Be("write_conflict");
    }

    // ---- reconciliations never overlap

    [Fact]
    public async Task Recompute_twoRunsAtOnceNeverOverlap_theSecondWaitsForTheFirstAndFindsNothingToDo()
    {
        var w = new PointsWorld();
        foreach (var day in new[] { "2026-09-14", "2026-09-15", "2026-09-16" })
        {
            Legacy(w, day, null, w.Occ.P1);
        }

        var release = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        w.Backfill.Pause = async () =>
        {
            w.Backfill.Pause = null;
            reached.SetResult();
            await release.Task;
        };

        var first = w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Startup, Ct);
        await reached.Task;
        var second = w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Nightly, Ct);
        await Task.Delay(50, Ct);
        w.Transactions.Runs.Should().Be(1, "the second run waits for the first");
        release.SetResult();
        var (a, b) = (await first, await second);

        a.AsT0.Created.Should().Be(3);
        b.AsT0.Should().BeEquivalentTo(new { Created = 0, Updated = 0, Removed = 0, SnapshotsSet = 0 });
        w.Ledger.Items.Should().HaveCount(3);
        w.PointsAudit(AuditAction.Recompute).Should().ContainSingle();
        w.Transactions.Runs.Should().Be(2);
    }

    [Fact]
    public async Task Recompute_aRunThatFailsReleasesTheGateForTheNext()
    {
        var w = new PointsWorld();
        w.Backfill.Failure = new PortError("points.failed: boom");

        var failed = await w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Startup, Ct);
        w.Backfill.Failure = null;
        var next = await w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Startup, Ct);

        failed.IsT2.Should().BeTrue();
        next.IsT0.Should().BeTrue();
    }

    // ---- the executions of one person are never mixed with the other kinds

    [Fact]
    public async Task Recompute_neverReadsUpdatesOrDeletesABookedRedemption()
    {
        var w = new PointsWorld();
        PointEntry Other(PointEntryKind kind, int amount) => new(w.Ledger.NextId(), "other:" + kind, kind, w.Occ.P1.Id, amount, OccurrenceWorld.At("2026-09-20"), OccurrenceWorld.At("2026-09-14"),
            null, null, null, string.Empty, PointEntrySource.Live, null, null, null, OccurrenceWorld.Now, OccurrenceWorld.Now);
        var redemption = Other(PointEntryKind.Redemption, -5);
        w.Ledger.Items.Add(redemption);

        var result = await Run(w);

        result.Should().BeEquivalentTo(new { Created = 0, Updated = 0, Removed = 0 });
        w.Ledger.Items.Should().BeEquivalentTo([redemption]);
    }
}
