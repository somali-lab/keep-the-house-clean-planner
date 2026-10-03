using Huishoudplanner.Application.Tests.Occurrences;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Application.Tests.Points;

/// <summary>
/// The live sync of one execution entry (ADR-0011): <c>points.test.ts</c>, the scenarios that are about the entry and its audit trail. The HTTP
/// routes that cause the sync are in the integration tests; the bonus entries, the redemptions, the progress and the badges are slices 4.2 to 4.5.
/// </summary>
public sealed class PointsSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Key(Occurrence occurrence) => "execution:" + occurrence.Id;

    private static Task<SyncOutcome> Sync(PointsWorld w, Occurrence occurrence, PointsSyncReason reason, AuditActor? actor = null) =>
        SyncAsync(w, occurrence.Id, reason, actor);

    private static async Task<SyncOutcome> SyncAsync(PointsWorld w, string id, PointsSyncReason reason, AuditActor? actor = null) =>
        (await w.Service.SyncAsync(actor ?? PointsWorld.Actor(w.Occ.P1), id, reason, Ct)).AsT0;

    // ---- a check-off creates exactly one entry

    [Fact]
    public async Task Sync_aDoneOccurrenceCreatesOneLiveEntryForThePersonWhoDidTheWork()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);

        var outcome = await Sync(w, occurrence, PointsSyncReason.Complete);

        outcome.Should().Be(SyncOutcome.Created);
        var entry = w.Ledger.Items.Should().ContainSingle().Subject;
        entry.Should().BeEquivalentTo(new
        {
            Key = Key(occurrence),
            Kind = PointEntryKind.Execution,
            PersonId = w.Occ.P1.Id,
            Amount = 30,
            Date = OccurrenceWorld.At("2026-09-16"),
            WeekStart = OccurrenceWorld.At("2026-09-14"),
            PeriodStart = (DateTimeOffset?)null,
            OccurrenceId = occurrence.Id,
            TaskId = w.Occ.Weekly.Id,
            TitleSnapshot = "Badkamer schoonmaken",
            Source = PointEntrySource.Live,
        });
    }

    [Fact]
    public async Task Sync_auditsTheCreationAsItsOwnPointsEntryWithTheOccurrenceAndTheReason()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);

        await Sync(w, occurrence, PointsSyncReason.Complete);

        var entry = w.Ledger.Items.Single();
        var audit = w.PointsAudit().Should().ContainSingle().Subject;
        (audit.Entity, audit.Action, audit.EntityId, audit.Actor.ActorId).Should().Be((AuditEntity.Points, AuditAction.Create, entry.Id, w.Occ.P1.Id));
        audit.Meta.Should().Be(AuditObject.Of(("occurrenceId", new AuditObjectId(occurrence.Id)), ("reason", "complete")));
        audit.Before.Count.Should().Be(0);
        audit.After["key"].Should().Be(new AuditString(Key(occurrence)));
        audit.After["personId"].Should().Be(new AuditObjectId(w.Occ.P1.Id));
        audit.After["amount"].Should().Be(new AuditInteger(30));
        audit.After["periodStart"].Should().Be(AuditNull.Instance);
        audit.After["source"].Should().Be(new AuditString("live"));
        audit.After["titleSnapshot"].Should().Be(new AuditString("Badkamer schoonmaken"));
    }

    [Fact]
    public async Task Sync_asecondSyncOfTheSameStateWritesAndAuditsNothing()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        await Sync(w, occurrence, PointsSyncReason.Complete);
        var (writes, audits) = (w.Ledger.Writes, w.Occ.Audit.Entries.Count);

        var outcome = await Sync(w, occurrence, PointsSyncReason.Correction);

        outcome.Should().Be(SyncOutcome.Unchanged);
        (w.Ledger.Writes, w.Occ.Audit.Entries.Count).Should().Be((writes, audits));
        w.Ledger.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Sync_aTaskOfZeroPointsEarnsNoEntryAndAuditsNothing()
    {
        var w = new PointsWorld();
        var free = w.Occ.NewTask("Planten water geven", 10, points: 0);
        var occurrence = w.Done(free, "2026-09-16", w.Occ.P1, w.Occ.P1);

        var outcome = await Sync(w, occurrence, PointsSyncReason.Complete);

        outcome.Should().Be(SyncOutcome.Unchanged);
        w.Ledger.Items.Should().BeEmpty();
        w.Occ.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_creditsTheCompletedByPersonNotTheAssignee()
    {
        var w = new PointsWorld();
        var onBehalf = w.Done(w.Occ.Weekly, "2026-09-17", w.Occ.P2, w.Occ.P1);
        var takeOver = w.Done(w.Occ.Weekly, "2026-09-24", w.Occ.P1, w.Occ.P1);
        var unassigned = w.Done(w.Occ.Weekly, "2026-09-18", null, w.Occ.P2);

        foreach (var occurrence in new[] { onBehalf, takeOver, unassigned })
        {
            await Sync(w, occurrence, PointsSyncReason.Complete);
        }

        w.Ledger.Items.ToDictionary(e => e.OccurrenceId!, e => e.PersonId).Should().Equal(new Dictionary<string, string>
        {
            [onBehalf.Id] = w.Occ.P1.Id,
            [takeOver.Id] = w.Occ.P1.Id,
            [unassigned.Id] = w.Occ.P2.Id,
        });
    }

    [Fact]
    public async Task Sync_olderDataWithoutCompletedByCreditsTheAssignee_andWithoutAssigneeEarnsNothing()
    {
        var w = new PointsWorld();
        var assigned = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P2, null);
        var nobody = w.Done(w.Occ.Weekly, "2026-09-17", null, null);

        await Sync(w, assigned, PointsSyncReason.Correction);
        var outcome = await Sync(w, nobody, PointsSyncReason.Correction);

        w.Ledger.Items.Should().ContainSingle().Which.PersonId.Should().Be(w.Occ.P2.Id);
        outcome.Should().Be(SyncOutcome.Unchanged);
    }

    // ---- uncomplete, retract and deletion remove it

    [Fact]
    public async Task Sync_anOccurrenceThatIsNoLongerDoneDeletesItsEntryWithTheRemovedFieldsInTheAudit()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        await Sync(w, occurrence, PointsSyncReason.Complete);
        var entry = w.Ledger.Items.Single();
        w.Occ.Occurrences.Items[w.Occ.Occurrences.Items.FindIndex(o => o.Id == occurrence.Id)] =
            occurrence with { Status = OccurrenceStatus.Open, PointsSnapshot = null, CompletedBy = null, CompletedAt = null };

        var outcome = await Sync(w, occurrence, PointsSyncReason.Uncomplete);

        outcome.Should().Be(SyncOutcome.Deleted);
        w.Ledger.Items.Should().BeEmpty();
        var audit = w.PointsAudit(AuditAction.Delete).Should().ContainSingle().Subject;
        audit.EntityId.Should().Be(entry.Id);
        audit.Meta.Should().Be(AuditObject.Of(("occurrenceId", new AuditObjectId(occurrence.Id)), ("reason", "uncomplete")));
        audit.Before["amount"].Should().Be(new AuditInteger(30));
        audit.Before["personId"].Should().Be(new AuditObjectId(w.Occ.P1.Id));
        audit.After.Count.Should().Be(0);
    }

    [Fact]
    public async Task Sync_anOccurrenceThatNoLongerExistsRemovesAnOrphanedEntry()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        await Sync(w, occurrence, PointsSyncReason.Complete);
        w.Occ.Occurrences.Items.RemoveAll(o => o.Id == occurrence.Id);

        var outcome = await Sync(w, occurrence, PointsSyncReason.Correction);

        outcome.Should().Be(SyncOutcome.Deleted);
        w.Ledger.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_anOccurrenceThatNeverExistedWithoutAnEntryDoesNothing()
    {
        var w = new PointsWorld();

        var outcome = await SyncAsync(w, "0000000000000000000000ff", PointsSyncReason.Retract);

        outcome.Should().Be(SyncOutcome.Unchanged);
        w.Occ.Audit.Entries.Should().BeEmpty();
    }

    // ---- an administrator's correction moves it in place

    [Fact]
    public async Task Sync_aCorrectionOfThePersonAndTheDayMovesTheEntryInPlaceWithOneAuditedUpdate()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-17", w.Occ.P2, w.Occ.P2);
        await Sync(w, occurrence, PointsSyncReason.Complete);
        var stored = w.Ledger.Items.Single();
        var corrected = occurrence with { CompletedBy = w.Occ.P1.Id, Date = OccurrenceWorld.At("2026-09-15") };
        w.Occ.Occurrences.Items[w.Occ.Occurrences.Items.FindIndex(o => o.Id == occurrence.Id)] = corrected;

        var outcome = await Sync(w, occurrence, PointsSyncReason.Correction);

        outcome.Should().Be(SyncOutcome.Updated);
        var moved = w.Ledger.Items.Single();
        (moved.Id, moved.PersonId, moved.Date, moved.WeekStart, moved.Amount, moved.Source).Should().Be(
            (stored.Id, w.Occ.P1.Id, OccurrenceWorld.At("2026-09-15"), OccurrenceWorld.At("2026-09-14"), 30, PointEntrySource.Live));
        var audit = w.PointsAudit(AuditAction.Update).Should().ContainSingle().Subject;
        audit.EntityId.Should().Be(stored.Id);
        // The diff lists the changed fields only, so the title and the amount travel in the meta for the history feed.
        audit.Before["personId"].Should().Be(new AuditObjectId(w.Occ.P2.Id));
        audit.After["personId"].Should().Be(new AuditObjectId(w.Occ.P1.Id));
        audit.Before["date"].Should().Be(new AuditInstant(OccurrenceWorld.At("2026-09-17")));
        audit.After["date"].Should().Be(new AuditInstant(OccurrenceWorld.At("2026-09-15")));
        audit.Before.Keys.Should().BeEquivalentTo("personId", "date");
        audit.Meta!["reason"].Should().Be(new AuditString("correction"));
        audit.Meta["titleSnapshot"].Should().Be(new AuditString("Badkamer schoonmaken"));
        audit.Meta["amount"].Should().Be(new AuditInteger(30));
    }

    [Fact]
    public async Task Sync_aCorrectionThatOnlyChangesTheTimeOfDayLeavesTheLedgerAlone()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-17", w.Occ.P2, w.Occ.P2);
        await Sync(w, occurrence, PointsSyncReason.Complete);
        var (writes, audits) = (w.Ledger.Writes, w.Occ.Audit.Entries.Count);
        w.Occ.Occurrences.Items[w.Occ.Occurrences.Items.FindIndex(o => o.Id == occurrence.Id)] = occurrence with { CompletedAt = occurrence.CompletedAt!.Value.AddMinutes(1) };

        var outcome = await Sync(w, occurrence, PointsSyncReason.Correction);

        outcome.Should().Be(SyncOutcome.Unchanged);
        (w.Ledger.Writes, w.Occ.Audit.Entries.Count).Should().Be((writes, audits));
    }

    [Fact]
    public async Task Sync_anEntryThatOnlyDiffersInItsSourceIsNotRewritten()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        await Sync(w, occurrence, PointsSyncReason.Complete);
        w.Ledger.Items[0] = w.Ledger.Items[0] with { Source = PointEntrySource.Backfill };
        var writes = w.Ledger.Writes;

        var outcome = await Sync(w, occurrence, PointsSyncReason.Correction);

        outcome.Should().Be(SyncOutcome.Unchanged);
        w.Ledger.Writes.Should().Be(writes);
        w.Ledger.Items[0].Source.Should().Be(PointEntrySource.Backfill);
    }

    // ---- what the sync leaves alone, and what it does when something fails

    [Fact]
    public async Task Sync_withoutSettingsTheLedgerCannotBeDatedAndIsLeftAlone()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        w.Occ.SettingsStore.Document = null;

        var outcome = await Sync(w, occurrence, PointsSyncReason.Complete);

        outcome.Should().Be(SyncOutcome.Unchanged);
        w.Ledger.Items.Should().BeEmpty();
        w.Occ.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_anEntryOfAnotherKindIsNeverTouched()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        var bonus = new PointEntry(w.Ledger.NextId(), "bonus_week_done:" + w.Occ.P1.Id + ":2026-09-14", PointEntryKind.BonusWeekDone, w.Occ.P1.Id, 10,
            OccurrenceWorld.At("2026-09-20"), OccurrenceWorld.At("2026-09-14"), OccurrenceWorld.At("2026-09-14"), null, null, string.Empty, PointEntrySource.Recompute, null, null, null,
            OccurrenceWorld.Now, OccurrenceWorld.Now);
        w.Ledger.Items.Add(bonus);

        await Sync(w, occurrence, PointsSyncReason.Complete);

        w.Ledger.Items.Should().Contain(bonus).And.HaveCount(2);
    }

    [Fact]
    public async Task Sync_aFailingLedgerWriteIsAPortErrorAndLeavesNothingBehind()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        w.Ledger.WriteFailure = new PortError("pointEntries.failed: boom");

        var result = await w.Service.SyncAsync(PointsWorld.Actor(w.Occ.P1), occurrence.Id, PointsSyncReason.Complete, Ct);

        result.AsT1.Message.Should().StartWith("pointEntries.failed");
        w.Transactions.Aborts.Should().Be(1);
        w.Ledger.Items.Should().BeEmpty();
        w.Occ.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_aFailingAuditWriteRollsTheLedgerEntryBackSoTheyNeverPartlyExist()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        w.Occ.Audit.Failure = new PortError("audit.failed: boom");

        var result = await w.Service.SyncAsync(PointsWorld.Actor(w.Occ.P1), occurrence.Id, PointsSyncReason.Complete, Ct);

        result.IsT1.Should().BeTrue();
        w.Transactions.Aborts.Should().Be(1);
        w.Ledger.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_aConcurrentWriterThatKeepsWinningIsAPortErrorForTheCaller()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        w.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "lost");

        var result = await w.Service.SyncAsync(PointsWorld.Actor(w.Occ.P1), occurrence.Id, PointsSyncReason.Complete, Ct);

        result.AsT1.Message.Should().StartWith("points.write_conflict");
    }

    [Fact]
    public async Task Sync_anUnreadableOccurrenceStoreIsAPortError()
    {
        var w = new PointsWorld();
        w.Occ.Occurrences.Failure = new PortError("occurrences.failed: boom");

        var result = await w.Service.SyncAsync(PointsWorld.Actor(w.Occ.P1), "0000000000000000000000ff", PointsSyncReason.Complete, Ct);

        result.AsT1.Message.Should().StartWith("occurrences.failed");
    }
}
