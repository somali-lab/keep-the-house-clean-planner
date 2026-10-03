using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Points;

namespace Huishoudplanner.Domain.Tests.Points;

/// <summary>How the ledger appears in the audit log (requirements 4.9): each real change of an execution entry, and the one summary of a reconciliation.</summary>
public sealed class PointsAuditTests
{
    private const string P1 = "0000000000000000000000a1";
    private const string P2 = "0000000000000000000000a2";
    private const string Occurrence = "0000000000000000000000c1";
    private const string Task = "0000000000000000000000b1";
    private const string EntryId = "0000000000000000000000e1";

    private static readonly AuditActor Actor = new(P1, AuditSource.Ui);
    private static readonly DateTimeOffset Day = new(2026, 9, 15, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Monday = new(2026, 9, 13, 22, 0, 0, TimeSpan.Zero);

    private static PointEntry Entry(string person = P1, int amount = 30, string? task = Task, string title = "Stofzuigen", DateTimeOffset? date = null) =>
        new(EntryId, "execution:" + Occurrence, PointEntryKind.Execution, person, amount, date ?? Day, Monday, null, Occurrence, task, title, PointEntrySource.Live, null, null, null, Day, Day);

    private static ExecutionEntryFields Fields(string person = P1, int amount = 30, string? task = Task, string title = "Stofzuigen", DateTimeOffset? date = null) =>
        new(person, amount, date ?? Day, Monday, Occurrence, task, title);

    // ---- create and delete list every field

    [Fact]
    public void ForCreate_listsEveryFieldInAfterButNotTheIdOrTheTimestamps()
    {
        var entry = PointsAudit.ForCreate(Actor, Entry(), Occurrence, PointsSyncReason.Complete);

        (entry.Entity, entry.Action, entry.EntityId, entry.Actor).Should().Be((AuditEntity.Points, AuditAction.Create, EntryId, Actor));
        entry.Before.Count.Should().Be(0);
        entry.After.Keys.Should().BeEquivalentTo("key", "kind", "personId", "amount", "date", "weekStart", "periodStart", "occurrenceId", "taskId", "titleSnapshot", "source");
        entry.After["key"].Should().Be(new AuditString("execution:" + Occurrence));
        entry.After["kind"].Should().Be(new AuditString("execution"));
        entry.After["personId"].Should().Be(new AuditObjectId(P1));
        entry.After["periodStart"].Should().Be(AuditNull.Instance);
        entry.After["source"].Should().Be(new AuditString("live"));
        entry.Meta.Should().Be(AuditObject.Of(("occurrenceId", new AuditObjectId(Occurrence)), ("reason", "complete")));
    }

    [Fact]
    public void ForCreate_aOneOffTaskKeepsAnExplicitNullTaskId() =>
        PointsAudit.ForCreate(Actor, Entry(task: null), Occurrence, PointsSyncReason.Recorded).After["taskId"].Should().Be(AuditNull.Instance);

    [Fact]
    public void ForDelete_listsEveryFieldInBeforeAndNothingInAfter()
    {
        var entry = PointsAudit.ForDelete(Actor, Entry(), Occurrence, PointsSyncReason.Retract);

        (entry.Action, entry.After.Count).Should().Be((AuditAction.Delete, 0));
        entry.Before["amount"].Should().Be(new AuditInteger(30));
        entry.Meta!["reason"].Should().Be(new AuditString("retract"));
    }

    [Theory]
    [InlineData(PointsSyncReason.Complete, "complete")]
    [InlineData(PointsSyncReason.Recorded, "recorded")]
    [InlineData(PointsSyncReason.Uncomplete, "uncomplete")]
    [InlineData(PointsSyncReason.Retract, "retract")]
    [InlineData(PointsSyncReason.Correction, "correction")]
    public void TheReasonIsTheWireNameOfTheSyncReason(PointsSyncReason reason, string wire) =>
        PointNames.ToWire(reason).Should().Be(wire);

    // ---- update lists the changed fields only

    [Fact]
    public void ForUpdate_listsTheChangedFieldsOnlyAndPutsTheTitleAndAmountInTheMeta()
    {
        var later = Day.AddDays(-2);

        var entry = PointsAudit.ForUpdate(Actor, Entry(person: P2), Fields(P1, date: later), Occurrence, PointsSyncReason.Correction)!;

        entry.Action.Should().Be(AuditAction.Update);
        entry.Before.Should().Be(AuditObject.Of(("personId", new AuditObjectId(P2)), ("date", Day)));
        entry.After.Should().Be(AuditObject.Of(("personId", new AuditObjectId(P1)), ("date", later)));
        entry.Meta.Should().Be(AuditObject.Of(
            ("occurrenceId", new AuditObjectId(Occurrence)), ("reason", "correction"), ("titleSnapshot", "Stofzuigen"), ("amount", 30)));
    }

    [Fact]
    public void ForUpdate_anEntryThatAlreadyMatchesIsANoOpWithNoAuditEntry() =>
        PointsAudit.ForUpdate(Actor, Entry(), Fields(), Occurrence, PointsSyncReason.Correction).Should().BeNull();

    [Fact]
    public void ForUpdate_aDifferentSourceOrPeriodIsBookkeepingAndNeverAChange()
    {
        var backfilled = Entry() with { Source = PointEntrySource.Backfill, PeriodStart = Monday };

        PointsAudit.ForUpdate(Actor, backfilled, Fields(), Occurrence, PointsSyncReason.Correction).Should().BeNull();
    }

    [Fact]
    public void ForUpdate_aTaskThatBecameOneOffIsAChange()
    {
        var entry = PointsAudit.ForUpdate(Actor, Entry(), Fields(task: null), Occurrence, PointsSyncReason.Correction)!;

        entry.Before["taskId"].Should().Be(new AuditObjectId(Task));
        entry.After["taskId"].Should().Be(AuditNull.Instance);
    }

    // ---- the summary of a reconciliation

    [Fact]
    public void ForRecompute_isOneSummaryEntryWithTheFixedLedgerIdAndTheResultAsMeta()
    {
        var result = PointsRecomputeResult.Empty(PointsRecomputeTrigger.Nightly) with
        {
            TasksDefaulted = 1,
            SnapshotsSet = 2,
            Created = 3,
            Updated = 1,
            Removed = 1,
            Unattributed = 4,
            Skipped = 5,
            Corrections =
            [
                new PointsCorrection("execution:a", new PointsHolding(P1, 9), new PointsHolding(P2, 30)),
                new PointsCorrection("execution:b", new PointsHolding(P1, 4), null),
            ],
            CorrectionsTotal = 2,
        };

        var entry = PointsAudit.ForRecompute(AuditActor.System, result);

        (entry.Entity, entry.Action, entry.EntityId, entry.Actor).Should().Be((AuditEntity.Points, AuditAction.Recompute, "000000000000000000000002", AuditActor.System));
        (entry.Before.Count, entry.After.Count).Should().Be((0, 0));
        entry.Meta!.Keys.Should().BeEquivalentTo(
            "trigger", "tasksDefaulted", "snapshotsSet", "created", "updated", "removed", "unattributed", "skipped", "corrections", "correctionsTotal",
            "correctionsTruncated", "bonusesCreated", "bonusesRemoved", "bonusChanges", "bonusChangesTotal", "bonusChangesTruncated");
        entry.Meta["trigger"].Should().Be(new AuditString("nightly"));
        entry.Meta["created"].Should().Be(new AuditInteger(3));
        entry.Meta["correctionsTruncated"].Should().Be(new AuditBool(false));
        var corrections = ((AuditArray)entry.Meta["corrections"]!).Items;
        corrections[0].Should().Be(AuditObject.Of(
            ("key", "execution:a"),
            ("from", AuditObject.Of(("personId", P1), ("amount", 9))),
            ("to", AuditObject.Of(("personId", P2), ("amount", 30)))));
        // Byte-compatible with the Node audit documents: the people inside the summary are hexadecimal strings, not ObjectIds.
        ((AuditObject)((AuditObject)corrections[0]!)["from"]!)["personId"].Should().BeOfType<AuditString>();
        ((AuditObject)corrections[1]!)["to"].Should().Be(AuditNull.Instance);
    }

    [Fact]
    public void ForRecompute_aBonusChangeCarriesItsPersonAsAHexString()
    {
        var result = PointsRecomputeResult.Empty(PointsRecomputeTrigger.Nightly) with { BonusChanges = [new PointsBonusChange("k", P1, 10, "created")], BonusChangesTotal = 1 };

        var change = (AuditObject)((AuditArray)PointsAudit.ForRecompute(AuditActor.System, result).Meta!["bonusChanges"]!).Items[0];

        change["personId"].Should().Be(new AuditString(P1));
    }

    [Theory]
    [InlineData(PointsRecomputeTrigger.Startup, "startup")]
    [InlineData(PointsRecomputeTrigger.Nightly, "nightly")]
    [InlineData(PointsRecomputeTrigger.Import, "import")]
    [InlineData(PointsRecomputeTrigger.Admin, "admin")]
    public void TheTriggerIsTheWireNameOfTheTrigger(PointsRecomputeTrigger trigger, string wire) =>
        PointNames.ToWire(trigger).Should().Be(wire);
}
