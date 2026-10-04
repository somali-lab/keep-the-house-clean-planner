using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;

namespace Huishoudplanner.Application.Tests.Badges;

/// <summary>
/// The evaluation of the derived awards (ADR-0014; the award scenarios of <c>badges.test.ts</c> that do not need the occurrence use cases): who holds what
/// and since when, the audit of a live change and of a reconciliation, idempotence and what is read.
/// </summary>
public sealed class BadgeAwardServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AuditActor Actor = AuditActor.From(BadgeWorld.Admin);

    private static DateTimeOffset At(int day, int hour = 8) => new(2026, 9, day, hour, 0, 0, TimeSpan.Zero);

    private static BadgeEvaluationScope Persons(params string[] people) => new(people);

    private static Task Live(BadgeWorld w, BadgeEvaluationScope scope, PointsSyncReason reason = PointsSyncReason.Complete) =>
        w.Evaluation.EvaluateAfterExecutionAsync(Actor, scope, reason, Ct);

    /// <summary>The live evaluation joins the transaction of its caller: the fake runs one around it.</summary>
    private static async Task InTransaction(BadgeWorld w, Func<Task> work) =>
        await w.Transactions.RunAsync(async _ =>
        {
            await work();
            return TransactionOutcome.Commit(0);
        }, Ct);

    // ---- a live evaluation

    [Fact]
    public async Task Live_awardsTheBadgeAtTheMomentTheThresholdWasCrossed_andAuditsItOnceWithTheReasonAndTheName()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toiletjuffrouw", BadgeWorld.Executions(2, w.Toilet.Id));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(16));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(17, 9));

        await InTransaction(w, () => Live(w, Persons(BadgeWorld.P1)));

        var award = w.Awards.Items.Should().ContainSingle().Subject;
        (award.BadgeId, award.PersonId, award.AwardedAt, award.Key).Should().Be((badge.Id, BadgeWorld.P1, At(17, 9), $"badge:{badge.Id}:{BadgeWorld.P1}"));
        var entry = w.Audit.Entries.Should().ContainSingle().Subject;
        (entry.Entity, entry.Action, entry.Actor).Should().Be((AuditEntity.BadgeAward, AuditAction.Create, Actor));
        entry.Meta.Should().Be(AuditObject.Of(("reason", "complete"), ("badgeName", "Toiletjuffrouw")));
        entry.After["awardedAt"].Should().Be(new AuditInstant(At(17, 9)));
    }

    [Fact]
    public async Task Live_belowTheThreshold_writesNothingAndAuditsNothing()
    {
        var w = new BadgeWorld();
        w.Seed("Toilet", BadgeWorld.Executions(2, w.Toilet.Id));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(16));

        await InTransaction(w, () => Live(w, Persons(BadgeWorld.P1)));

        w.Awards.Applies.Should().Be(0);
        w.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Live_aThirdExecution_leavesTheAwardWhereItWas_andWritesNothing()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toilet", BadgeWorld.Executions(2, w.Toilet.Id));
        w.SeedAward(badge, BadgeWorld.P1, At(17));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(16));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(17));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(18));

        await InTransaction(w, () => Live(w, Persons(BadgeWorld.P1)));

        w.Awards.Applies.Should().Be(0);
        w.Audit.Entries.Should().BeEmpty();
        w.Awards.Items.Single().AwardedAt.Should().Be(At(17));
    }

    [Fact]
    public async Task Live_anUndoBelowTheThreshold_revokesTheAward_withTheReason()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toilet", BadgeWorld.Executions(2, w.Toilet.Id));
        w.SeedAward(badge, BadgeWorld.P1, At(17));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(16));

        await InTransaction(w, () => Live(w, Persons(BadgeWorld.P1), PointsSyncReason.Uncomplete));

        w.Awards.Items.Should().BeEmpty();
        var entry = w.Audit.Entries.Should().ContainSingle().Subject;
        (entry.Action, entry.Meta!["reason"]).Should().Be((AuditAction.Delete, new AuditString("uncomplete")));
    }

    [Fact]
    public async Task Live_earlierWorkMovesTheMomentOfTheAward_asAnUpdate()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toilet", BadgeWorld.Executions(2, w.Toilet.Id));
        w.SeedAward(badge, BadgeWorld.P1, At(18));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(16));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(17));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(18));

        await InTransaction(w, () => Live(w, Persons(BadgeWorld.P1), PointsSyncReason.Correction));

        w.Awards.Items.Single().AwardedAt.Should().Be(At(17));
        var entry = w.Audit.Entries.Should().ContainSingle().Subject;
        (entry.Action, entry.Before["awardedAt"], entry.After["awardedAt"]).Should().Be((AuditAction.Update, new AuditInstant(At(18)), new AuditInstant(At(17))));
        entry.Meta!["reason"].Should().Be(new AuditString("correction"));
    }

    [Fact]
    public async Task Live_onlyTheGivenPeopleAreLookedAt()
    {
        var w = new BadgeWorld();
        w.Seed("Toilet", BadgeWorld.Executions(1, w.Toilet.Id));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(16));
        w.Done(BadgeWorld.P2, w.Toilet.Id, At(16));

        await InTransaction(w, () => Live(w, Persons(BadgeWorld.P2)));

        w.Awards.Items.Should().ContainSingle().Which.PersonId.Should().Be(BadgeWorld.P2);
        w.Evidence.ExecutionReadsFor.Should().ContainSingle().Which.Should().Equal(BadgeWorld.P2);
    }

    [Fact]
    public async Task Live_aTaskNoRuleCovers_readsNoExecutions_andStillAwardsTheBadgeThatIsCovered()
    {
        var w = new BadgeWorld();
        var mop = w.Seed("Dweilen", BadgeWorld.Executions(1, w.Mop.Id));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(16));
        w.Done(BadgeWorld.P1, w.Mop.Id, At(16, 9));

        await InTransaction(w, () => Live(w, new BadgeEvaluationScope([BadgeWorld.P1], true, w.Toilet.Id)));
        w.Evidence.ExecutionReads.Should().Be(0);
        w.Awards.Items.Should().BeEmpty();

        await InTransaction(w, () => Live(w, new BadgeEvaluationScope([BadgeWorld.P1], true, w.Mop.Id)));
        w.Evidence.ExecutionReads.Should().Be(1);
        w.Awards.Items.Should().ContainSingle().Which.BadgeId.Should().Be(mop.Id);
    }

    [Fact]
    public async Task Live_aBadgeThatDoesNotCountExecutions_readsNone()
    {
        var w = new BadgeWorld();
        w.Seed("Op tijd", BadgeRule.OnTimeWeeks(2));

        await InTransaction(w, () => Live(w, new BadgeEvaluationScope([BadgeWorld.P1], true, w.Toilet.Id)));

        w.Evidence.ExecutionReads.Should().Be(0);
    }

    [Fact]
    public async Task Live_aFailingEvaluation_isSwallowed_andWritesNothing()
    {
        var w = new BadgeWorld();
        w.Seed("Toilet", BadgeWorld.Executions(1, w.Toilet.Id));
        w.Evidence.Failure = new PortError("evidence down");

        await InTransaction(w, () => Live(w, Persons(BadgeWorld.P1)));

        w.Awards.Items.Should().BeEmpty();
        w.Audit.Entries.Should().BeEmpty();
        w.Transactions.Aborts.Should().Be(0, "the check-off that called it must still commit");
    }

    [Fact]
    public async Task Live_aFailingAuditWrite_isSwallowed_too()
    {
        var w = new BadgeWorld();
        w.Seed("Toilet", BadgeWorld.Executions(1, w.Toilet.Id));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(16));
        w.Audit.Failure = new PortError("audit down");

        await InTransaction(w, () => Live(w, Persons(BadgeWorld.P1)));

        w.Transactions.Aborts.Should().Be(0);
    }

    [Fact]
    public async Task Live_onTimeWeeks_countTheBonusesOfThePerson()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Op tijd", BadgeRule.OnTimeWeeks(2));
        w.Evidence.Weeks.Add(new OnTimeWeek(BadgeWorld.P1, At(20)));
        w.Evidence.Weeks.Add(new OnTimeWeek(BadgeWorld.P1, At(27)));
        w.Evidence.Weeks.Add(new OnTimeWeek(BadgeWorld.P2, At(20)));

        await InTransaction(w, () => Live(w, BadgeEvaluationScope.Everybody));

        w.Awards.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { BadgeId = badge.Id, PersonId = BadgeWorld.P1, AwardedAt = At(27) });
    }

    // ---- the reconciliation

    [Fact]
    public async Task Reconcile_repairsDriftInOneSummary_alostAwardComesBackAtTheSameMoment_anAwardWithoutDataGoes()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toilet", BadgeWorld.Executions(1, w.Toilet.Id));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(16));
        w.SeedAward(badge, BadgeWorld.P2, At(16));

        var result = (await w.Evaluation.ReconcileAsync(AuditActor.System, BadgeEvalTrigger.Admin, null, Ct)).AsT0;

        result.Should().Be(new BadgeEvaluationResult(1, 0, 1));
        w.Awards.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new { PersonId = BadgeWorld.P1, AwardedAt = At(16) });
        var summary = w.Audit.Entries.Should().ContainSingle().Subject;
        (summary.Entity, summary.Action, summary.EntityId, summary.Actor).Should().Be((AuditEntity.BadgeAward, AuditAction.Recompute, "000000000000000000000003", AuditActor.System));
        summary.Meta!["trigger"].Should().Be(new AuditString("admin"));
        ((AuditArray)summary.Meta["changes"]!).Items.Cast<AuditObject>().Select(c => ((AuditString)c["change"]!).Value).Order().Should().Equal("created", "removed");
    }

    [Fact]
    public async Task Reconcile_isIdempotent_asecondRunWritesAndAuditsNothing()
    {
        var w = new BadgeWorld();
        w.Seed("Toilet", BadgeWorld.Executions(1, w.Toilet.Id));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(16));
        await w.Evaluation.ReconcileAsync(AuditActor.System, BadgeEvalTrigger.Nightly, null, Ct);
        var applies = w.Awards.Applies;
        var entries = w.Audit.Entries.Count;
        var snapshot = w.Awards.Items.ToList();

        var again = (await w.Evaluation.ReconcileAsync(AuditActor.System, BadgeEvalTrigger.Nightly, null, Ct)).AsT0;

        again.Should().Be(BadgeEvaluationResult.None);
        w.Awards.Applies.Should().Be(applies);
        w.Audit.Entries.Should().HaveCount(entries);
        w.Awards.Items.Should().Equal(snapshot);
    }

    [Fact]
    public async Task Reconcile_aDeletedBadgeKeepsItsNameInTheHistoryOfTheWithdrawnAward()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toiletjuffrouw", BadgeWorld.Executions(1, w.Toilet.Id));
        w.SeedAward(badge, BadgeWorld.P1, At(16));
        w.Badges.Items.Clear();

        await w.Evaluation.ReconcileAsync(Actor, BadgeEvalTrigger.Badge, new Dictionary<string, string> { [badge.Id] = "Toiletjuffrouw" }, Ct);

        var change = ((AuditArray)w.Audit.Entries.Single().Meta!["changes"]!).Items.Cast<AuditObject>().Single();
        change["badgeName"].Should().Be(new AuditString("Toiletjuffrouw"));
    }

    [Fact]
    public async Task Reconcile_aDeactivatedBadge_revokesItsAwards_andReactivatingItAwardsThemAgainAtTheSameMoment()
    {
        var w = new BadgeWorld();
        var badge = w.Seed("Toilet", BadgeWorld.Executions(1, w.Toilet.Id));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(17));
        w.SeedAward(badge, BadgeWorld.P1, At(17));
        w.Badges.Items[0] = badge with { Active = false };

        await w.Evaluation.ReconcileAsync(Actor, BadgeEvalTrigger.Badge, null, Ct);
        w.Awards.Items.Should().BeEmpty();
        w.Badges.Items[0] = badge;
        await w.Evaluation.ReconcileAsync(Actor, BadgeEvalTrigger.Badge, null, Ct);

        w.Awards.Items.Should().ContainSingle().Which.AwardedAt.Should().Be(At(17));
    }

    [Fact]
    public async Task Reconcile_aFailingRead_isAPortError_andLeavesNoSummary()
    {
        var w = new BadgeWorld();
        w.Seed("Toilet", BadgeWorld.Executions(1, w.Toilet.Id));
        w.Evidence.Failure = new PortError("evidence down");

        var result = await w.Evaluation.ReconcileAsync(Actor, BadgeEvalTrigger.Admin, null, Ct);

        result.AsT2.Message.Should().Contain("evidence down");
        w.Audit.Entries.Should().BeEmpty();
        w.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Reconcile_aFailingSummaryWrite_rollsTheAwardsBack()
    {
        var w = new BadgeWorld();
        w.Seed("Toilet", BadgeWorld.Executions(1, w.Toilet.Id));
        w.Done(BadgeWorld.P1, w.Toilet.Id, At(16));
        w.Audit.Failure = new PortError("audit down");

        var result = await w.Evaluation.ReconcileAsync(Actor, BadgeEvalTrigger.Admin, null, Ct);

        result.IsT2.Should().BeTrue();
        w.Awards.Items.Should().BeEmpty("an award never exists without its audit entry");
    }

    [Fact]
    public async Task Reconcile_aConflictOfTheTransaction_isReturned()
    {
        var w = new BadgeWorld();
        w.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "kept winning");

        var result = await w.Evaluation.ReconcileAsync(Actor, BadgeEvalTrigger.Admin, null, Ct);

        result.AsT1.Code.Should().Be("write_conflict");
    }

    [Fact]
    public async Task ReconcileSafely_neverFails_whateverTheEvaluationDoes()
    {
        var w = new BadgeWorld();
        w.Seed("Toilet", BadgeWorld.Executions(1, w.Toilet.Id));
        w.Evidence.Failure = new PortError("evidence down");
        await w.Evaluation.ReconcileSafelyAsync(Actor, BadgeEvalTrigger.Reset, null, Ct);

        w.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "kept winning");
        await w.Evaluation.ReconcileSafelyAsync(Actor, BadgeEvalTrigger.Reset, null, Ct);

        w.Awards.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_withoutBadgesOrAwards_readsNoDataAndWritesNothing()
    {
        var w = new BadgeWorld();

        var result = (await w.Evaluation.ReconcileAsync(Actor, BadgeEvalTrigger.Startup, null, Ct)).AsT0;

        result.Should().Be(BadgeEvaluationResult.None);
        w.Evidence.ExecutionReads.Should().Be(0);
        w.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_aSummaryListsAtMost100Changes_andSaysSo()
    {
        var w = new BadgeWorld();
        w.Seed("Alles", BadgeWorld.Executions(1));
        for (var i = 0; i < 101; i++)
        {
            w.Done($"b{i:x23}", w.Toilet.Id, At(16));
        }

        await w.Evaluation.ReconcileAsync(Actor, BadgeEvalTrigger.Import, null, Ct);

        var meta = w.Audit.Entries.Single().Meta!;
        (meta["created"], meta["changesTotal"], meta["changesTruncated"]).Should().Be((new AuditInteger(101), new AuditInteger(101), new AuditBool(true)));
        ((AuditArray)meta["changes"]!).Items.Should().HaveCount(100);
    }
}
