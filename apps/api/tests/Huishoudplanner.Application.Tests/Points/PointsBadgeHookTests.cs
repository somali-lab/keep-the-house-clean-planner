using Huishoudplanner.Application.Points;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;

namespace Huishoudplanner.Application.Tests.Points;

/// <summary>
/// The badge step of the points use cases (ADR-0014, slice 4.5): the live evaluation after every execution sync for the people and the task it concerns,
/// and the third transaction of the reconciliation. What the evaluation does with the data is in <c>BadgeAwardServiceTests</c> and the integration tests.
/// </summary>
public sealed class PointsBadgeHookTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Sync(PointsWorld w, string occurrenceId, PointsSyncReason reason) =>
        (await w.Service.SyncAsync(PointsWorld.Actor(w.Occ.P1), occurrenceId, reason, Ct)).IsT0.Should().BeTrue();

    private static Occurrence Reopen(PointsWorld w, Occurrence occurrence)
    {
        var reopened = occurrence with { Status = OccurrenceStatus.Open, StatusBeforeCompletion = null, CompletedAt = null, CompletedBy = null };
        w.Occ.Occurrences.Items[w.Occ.Occurrences.Items.FindIndex(o => o.Id == occurrence.Id)] = reopened;
        return reopened;
    }

    // ---- the live evaluation

    [Fact]
    public async Task Sync_aCheckOff_evaluatesThePersonWhoDidTheWork_forTheTaskOfTheExecution()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P2);

        await Sync(w, occurrence.Id, PointsSyncReason.Complete);

        var call = w.Badges.Live.Should().ContainSingle().Subject;
        call.Actor.ActorId.Should().Be(w.Occ.P1.Id);
        call.Reason.Should().Be(PointsSyncReason.Complete);
        call.Scope.PersonIds.Should().Equal([w.Occ.P2.Id], "completedBy decides, not the assignee");
        (call.Scope.TaskKnown, call.Scope.TaskId).Should().Be((true, w.Occ.Weekly.Id));
    }

    [Fact]
    public async Task Sync_workOfAnAssigneeWithoutCompletedBy_isCreditedToTheAssignee()
    {
        var w = new PointsWorld();
        var occurrence = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, null);

        await Sync(w, occurrence.Id, PointsSyncReason.Complete);

        w.Badges.Live.Single().Scope.PersonIds.Should().Equal(w.Occ.P1.Id);
    }

    [Fact]
    public async Task Sync_anUndo_evaluatesTheHolderOfTheEntryItRemoves()
    {
        var w = new PointsWorld();
        var done = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P2);
        await Sync(w, done.Id, PointsSyncReason.Complete);
        w.Badges.Live.Clear();
        Reopen(w, done);

        await Sync(w, done.Id, PointsSyncReason.Uncomplete);

        w.Ledger.Items.Should().BeEmpty();
        var scope = w.Badges.Live.Should().ContainSingle().Subject.Scope;
        scope.PersonIds.Should().Equal(w.Occ.P2.Id);
        (scope.TaskKnown, scope.TaskId).Should().Be((true, w.Occ.Weekly.Id));
    }

    [Fact]
    public async Task Sync_aCorrectionThatMovesTheWork_evaluatesBothThePreviousAndTheNewPerson()
    {
        var w = new PointsWorld();
        var done = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        await Sync(w, done.Id, PointsSyncReason.Complete);
        w.Badges.Live.Clear();
        w.Occ.Occurrences.Items[w.Occ.Occurrences.Items.FindIndex(o => o.Id == done.Id)] = done with { CompletedBy = w.Occ.P2.Id };

        await Sync(w, done.Id, PointsSyncReason.Correction);

        w.Badges.Live.Single().Scope.PersonIds.Should().BeEquivalentTo([w.Occ.P1.Id, w.Occ.P2.Id]);
    }

    [Theory]
    [InlineData(PointsSyncReason.Uncomplete)]
    [InlineData(PointsSyncReason.Retract)]
    [InlineData(PointsSyncReason.Correction)]
    public async Task Sync_workThatEarnedNoEntryAndIsGone_doesNotSayWhoHeldIt_soEverybodyIsEvaluated(PointsSyncReason reason)
    {
        var w = new PointsWorld();
        var open = w.Occ.Seed(w.Occ.Weekly, "2026-09-16", w.Occ.P1);

        await Sync(w, open.Id, reason);

        var scope = w.Badges.Live.Should().ContainSingle().Subject.Scope;
        scope.PersonIds.Should().BeNull();
        (scope.TaskKnown, scope.TaskId).Should().Be((true, w.Occ.Weekly.Id));
    }

    [Theory]
    [InlineData(PointsSyncReason.Complete)]
    [InlineData(PointsSyncReason.Recorded)]
    public async Task Sync_whenNobodyIsAffected_evaluatesNothing(PointsSyncReason reason)
    {
        var w = new PointsWorld();
        var open = w.Occ.Seed(w.Occ.Weekly, "2026-09-16", w.Occ.P1);

        await Sync(w, open.Id, reason);

        w.Badges.Live.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_aDeletedOccurrence_evaluatesTheHolderOfItsEntry_withTheTaskOfTheEntry()
    {
        var w = new PointsWorld();
        var done = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        await Sync(w, done.Id, PointsSyncReason.Complete);
        w.Badges.Live.Clear();
        w.Occ.Occurrences.Items.RemoveAll(o => o.Id == done.Id);

        await Sync(w, done.Id, PointsSyncReason.Correction);

        var scope = w.Badges.Live.Should().ContainSingle().Subject.Scope;
        scope.PersonIds.Should().Equal(w.Occ.P1.Id);
        (scope.TaskKnown, scope.TaskId).Should().Be((true, w.Occ.Weekly.Id));
    }

    [Fact]
    public async Task Sync_aGoneOccurrenceWithoutEntryOrTask_evaluatesEverybodyWithoutAKnownTask()
    {
        var w = new PointsWorld();

        await Sync(w, "0000000000000000000000ff", PointsSyncReason.Retract);

        var scope = w.Badges.Live.Should().ContainSingle().Subject.Scope;
        (scope.PersonIds, scope.TaskKnown).Should().Be((null, false));
    }

    [Fact]
    public async Task Sync_withoutSettings_leavesTheLedgerAloneButStillEvaluatesTheBadges()
    {
        var w = new PointsWorld();
        var done = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        w.Occ.SettingsStore.Document = null;

        await Sync(w, done.Id, PointsSyncReason.Complete);

        w.Ledger.Items.Should().BeEmpty();
        w.Badges.Live.Should().ContainSingle();
    }

    [Fact]
    public async Task Sync_aLedgerStepThatFails_isNotFollowedByTheBadges()
    {
        var w = new PointsWorld();
        var done = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        w.Ledger.WriteFailure = new PortError("pointEntries.failed: down");

        var result = await w.Service.SyncAsync(PointsWorld.Actor(w.Occ.P1), done.Id, PointsSyncReason.Complete, Ct);

        result.IsT1.Should().BeTrue();
        w.Badges.Live.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_aChangeThatChangesNothing_stillEvaluatesTheBadges_becauseTheirDataIsOtherThanTheLedger()
    {
        var w = new PointsWorld();
        var done = w.Done(w.Occ.Weekly, "2026-09-16", w.Occ.P1, w.Occ.P1);
        await Sync(w, done.Id, PointsSyncReason.Complete);
        w.Badges.Live.Clear();

        await Sync(w, done.Id, PointsSyncReason.Complete);

        w.Badges.Live.Should().ContainSingle();
    }

    // ---- the reconciliation

    [Theory]
    [InlineData(PointsRecomputeTrigger.Startup, BadgeEvalTrigger.Startup)]
    [InlineData(PointsRecomputeTrigger.Nightly, BadgeEvalTrigger.Nightly)]
    [InlineData(PointsRecomputeTrigger.Import, BadgeEvalTrigger.Import)]
    [InlineData(PointsRecomputeTrigger.Admin, BadgeEvalTrigger.Admin)]
    public async Task Recompute_endsWithTheBadgeStep_ofTheSameTrigger_asTheActorOfTheRun(PointsRecomputeTrigger trigger, BadgeEvalTrigger expected)
    {
        var w = new PointsWorld();

        var result = await w.Service.RecomputeAsync(AuditActor.System, trigger, Ct);

        result.IsT0.Should().BeTrue();
        var call = w.Badges.Reconciles.Should().ContainSingle().Subject;
        (call.Trigger, call.Safely, call.Actor, call.Names).Should().Be((expected, false, AuditActor.System, null));
    }

    [Fact]
    public async Task Recompute_theBadgeStepRunsAfterTheCommittedWorkOfTheEarlierSteps()
    {
        var w = new PointsWorld();
        w.Done(w.Occ.Weekly, "2026-09-14", w.Occ.P1, w.Occ.P1, snapshot: null);
        w.Badges.ReconcileResult = new PortError("badgeAwards.failed: down");

        var failed = await w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Nightly, Ct);

        failed.AsT2.Message.Should().StartWith(PointsService.BadgeStepFailed).And.Contain("down");
        w.Ledger.Items.Should().ContainSingle("a badge failure never rolls back the executions that were reconciled");
        w.PointsAudit(AuditAction.Recompute).Should().ContainSingle("the points summary is recorded before the failure is reported");
    }

    [Fact]
    public async Task Recompute_aBadgeConflict_isReturnedAsAConflict()
    {
        var w = new PointsWorld();
        w.Badges.ReconcileResult = new ConflictError("write_conflict", "kept winning");

        var failed = await w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Admin, Ct);

        failed.AsT1.Code.Should().Be("write_conflict");
    }

    [Fact]
    public async Task Recompute_theBadgeStepRunsAlsoWhenTheBonusStepFailed_andTheBonusFailureIsReported()
    {
        var w = new PointsWorld();
        w.Done(w.Occ.Weekly, "2026-09-14", w.Occ.P1, w.Occ.P1);
        w.Ledger.BonusFailure = new PortError("pointEntries.failed: bonuses down");
        w.Badges.ReconcileResult = new PortError("badgeAwards.failed: down too");

        var failed = await w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Nightly, Ct);

        w.Badges.Reconciles.Should().ContainSingle();
        failed.AsT2.Message.Should().StartWith(PointsService.BonusStepFailed);
    }

    [Fact]
    public async Task Recompute_anExecutionStepThatFails_isNotFollowedByTheBadgeStep()
    {
        var w = new PointsWorld();
        w.Ledger.Failure = new PortError("pointEntries.failed: down");

        var failed = await w.Service.RecomputeAsync(AuditActor.System, PointsRecomputeTrigger.Nightly, Ct);

        failed.IsT2.Should().BeTrue();
        w.Badges.Reconciles.Should().BeEmpty();
    }
}
