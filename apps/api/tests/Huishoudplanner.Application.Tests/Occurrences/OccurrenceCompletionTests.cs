using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;

namespace Huishoudplanner.Application.Tests.Occurrences;

/// <summary>
/// Completing, undoing, correcting and deleting a completion: <c>occurrences.test.ts</c> (the complete, uncomplete, edit_completion and
/// delete scenarios) and <c>completion-choice.test.ts</c>. Not here: the points ledger that follows a completion (phase 4).
/// </summary>
public sealed class OccurrenceCompletionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuditObjectId Id(string hex) => new(hex);

    // ---- complete: the choice (completion-choice.test.ts)

    [Fact]
    public async Task Complete_workOfSomeoneElseWithoutAChoiceIsRefusedAndWritesNothing()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P2);

        var result = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct);

        result.AsT2.Errors["completedBy"].Should().Equal("completion_choice_required");
        w.Writes.Should().Be(0);
        w.Stored(occurrence).Status.Should().Be(OccurrenceStatus.Open);
    }

    [Fact]
    public async Task Complete_takeOverCreditsTheActorAndMakesThemTheAssignee()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P1);

        var view = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P2), occurrence.Id, new CompleteCommand(TakeOver: true), Ct)).AsT0;

        view.Occurrence.Status.Should().Be(OccurrenceStatus.Done);
        (view.Occurrence.CompletedBy, view.Occurrence.AssigneeId).Should().Be((w.P2.Id, w.P2.Id));
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Complete).Single();
        entry.Actor.ActorId.Should().Be(w.P2.Id);
        var meta = entry.Meta!;
        meta["completedBy"].Should().Be(Id(w.P2.Id));
        meta["wasAssignee"].Should().Be(new AuditBool(false));
        meta["takenOver"].Should().Be(new AuditBool(true));
        meta["previousAssigneeId"].Should().Be(Id(w.P1.Id));
    }

    [Fact]
    public async Task Complete_onBehalfCreditsTheNamedPersonAndLeavesTheAssignee()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P2);

        var view = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(w.P2.Id), Ct)).AsT0;

        (view.Occurrence.CompletedBy, view.Occurrence.AssigneeId).Should().Be((w.P2.Id, w.P2.Id));
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Complete).Single();
        entry.Actor.ActorId.Should().Be(w.P1.Id);
        entry.Meta!["completedBy"].Should().Be(Id(w.P2.Id));
        entry.Meta["wasAssignee"].Should().Be(new AuditBool(true));
    }

    [Fact]
    public async Task Complete_bothChoicesAtOnceIsAConflictBeforeAnythingIsRead()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P2);

        var result = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(w.P2.Id, TakeOver: true), Ct);

        result.AsT2.Errors["completedBy"].Should().Equal("completion_choice_conflict");
        w.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Complete_workOfTheActorNeedsNoChoiceAndCreditsTheActor()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-19", w.P1);

        var view = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct)).AsT0;

        (view.Occurrence.CompletedBy, view.Occurrence.AssigneeId).Should().Be((w.P1.Id, w.P1.Id));
    }

    [Fact]
    public async Task Complete_unassignedWorkNeedsNoChoiceAndIsClaimedByTheActor()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-09-20");

        var view = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P2), occurrence.Id, new CompleteCommand(), Ct)).AsT0;

        (view.Occurrence.CompletedBy, view.Occurrence.AssigneeId).Should().Be((w.P2.Id, w.P2.Id));
        w.Entries(AuditEntity.Occurrence, AuditAction.Complete).Single().Meta!["claimed"].Should().Be(new AuditBool(true));
    }

    [Fact]
    public async Task Complete_unassignedWorkWithANamedPersonClaimsItForThatPerson()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-09-16");

        var view = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(w.P2.Id), Ct)).AsT0;

        (view.Occurrence.AssigneeId, view.Occurrence.CompletedBy).Should().Be((w.P2.Id, w.P2.Id));
    }

    [Fact]
    public async Task Complete_aSecondCompleteIsInvalidTransitionBeforeAChoiceIsAsked()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-17", w.P1);
        (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P2), occurrence.Id, new CompleteCommand(TakeOver: true), Ct)).IsT0.Should().BeTrue();

        var again = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct);

        var conflict = again.AsT3;
        conflict.Code.Should().Be("invalid_transition");
        conflict.Extensions.Should().Equal(new Dictionary<string, object?> { ["status"] = "done", ["action"] = "complete" });
    }

    // ---- complete: the person, the audit and what follows

    [Fact]
    public async Task Complete_creditsTheCompletedByPersonAuditsTheContextAndMaintainsLastCompletedAt()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-21", w.P1);

        var view = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P2), occurrence.Id, new CompleteCommand(w.P1.Id), Ct)).AsT0;

        view.Occurrence.Should().Match<Occurrence>(o =>
            o.Status == OccurrenceStatus.Done && o.StatusBeforeCompletion == OccurrenceStatus.Open && o.CompletedBy == w.P1.Id && o.CompletedAt == OccurrenceWorld.Now);
        view.Occurrence.PointsSnapshot.Should().Be(30);
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Complete).Single();
        entry.Actor.Should().Be(new AuditActor(w.P2.Id, AuditSource.Ui));
        var context = (AuditObject)entry.Meta!["occurrence"]!;
        context["taskNameSnapshot"].Should().Be(new AuditString("Badkamer schoonmaken"));
        context["roomNameSnapshot"].Should().Be(new AuditString("Badkamer"));
        context["date"].Should().Be(new AuditInstant(OccurrenceWorld.At("2026-09-21")));
        entry.After["completedBy"].Should().Be(Id(w.P1.Id));
        w.TaskStore.Items.Single(t => t.Id == w.Weekly.Id).LastCompletedAt.Should().Be(OccurrenceWorld.Now);
        var taskEntry = w.Entries(AuditEntity.Task).Single();
        taskEntry.Action.Should().Be(AuditAction.Update);
        taskEntry.After["lastCompletedAt"].Should().Be(new AuditInstant(OccurrenceWorld.Now));
        taskEntry.Meta!["occurrenceId"].Should().Be(Id(occurrence.Id));
    }

    [Fact]
    public async Task Complete_aSkippedOccurrenceRemembersThatForTheUndo()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-09-24", status: OccurrenceStatus.Skipped, tweak: o => o with { SkipReason = "geen tijd" });

        var view = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct)).AsT0;

        view.Occurrence.StatusBeforeCompletion.Should().Be(OccurrenceStatus.Skipped);
    }

    [Theory]
    [InlineData(null, 1, "unknown_user")]
    [InlineData("inactive", 1, "inactive_user")]
    public async Task Complete_theNamedPersonMustBeAnActivePerson(string? kind, int expectedErrors, string message)
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-11-02", w.P1);
        var who = kind == "inactive" ? w.People.Add("Weg", active: false).Id : "0123456789abcdef01234567";

        var result = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(who), Ct);

        result.AsT2.Errors.Should().HaveCount(expectedErrors);
        result.AsT2.Errors["completedBy"].Should().Equal(message);
        w.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Complete_aMalformedCompletedByIsAValidationError()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-11-02", w.P1);

        var result = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand("nope"), Ct);

        result.AsT2.Errors["completedBy"].Should().Equal("invalid_object_id");
    }

    [Fact]
    public async Task Complete_anUnknownOccurrenceIsNotFoundAndABadIdIsAValidationError()
    {
        var w = new OccurrenceWorld();

        var unknown = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), "0123456789abcdef01234567", new CompleteCommand(), Ct);
        var bad = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), "x", new CompleteCommand(), Ct);

        unknown.IsT1.Should().BeTrue();
        bad.AsT2.Errors["id"].Should().Equal("invalid_object_id");
    }

    [Fact]
    public async Task Complete_aOneOffTaskSnapshotsTheChosenPointsOrTheDurationRuleAndHasNoTaskToRefresh()
    {
        var w = new OccurrenceWorld();
        var chosen = w.Seed(w.Weekly, "2026-09-16", w.P1, tweak: o => o with { TaskId = null, PointsOverride = 7, DurationMinutesSnapshot = 45 });
        var byRule = w.Seed(w.Weekly, "2026-09-17", w.P1, tweak: o => o with { TaskId = null, DurationMinutesSnapshot = 45 });

        var first = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), chosen.Id, new CompleteCommand(), Ct)).AsT0;
        var second = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), byRule.Id, new CompleteCommand(), Ct)).AsT0;

        (first.Occurrence.PointsSnapshot, second.Occurrence.PointsSnapshot).Should().Be((7, 45));
        w.TaskStore.LastCompletedWrites.Should().Be(0);
        w.Entries(AuditEntity.Task).Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_aTaskThatIsGoneSnapshotsTheDurationRule()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P1);
        w.TaskStore.Items.Clear();

        var view = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct)).AsT0;

        view.Occurrence.PointsSnapshot.Should().Be(30);
    }

    [Fact]
    public async Task Complete_freezesThePeriodOwnerWhenThePlannedWeekHasEnded()
    {
        var w = new OccurrenceWorld();
        var late = w.Seed(w.Weekly, "2026-09-14", w.P1);
        var unassigned = w.Seed(w.Twice, "2026-09-14");
        var running = w.Seed(w.Weekly, "2026-09-21", w.P1);
        w.Clock.Now = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var thisWeek = w.Seed(w.Twice, "2026-09-22", w.P1);

        var a = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P2), late.Id, new CompleteCommand(TakeOver: true), Ct)).AsT0;
        var b = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P2), unassigned.Id, new CompleteCommand(), Ct)).AsT0;
        var c = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), running.Id, new CompleteCommand(), Ct)).AsT0;
        var d = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), thisWeek.Id, new CompleteCommand(), Ct)).AsT0;

        (a.Occurrence.PeriodOwnerFrozen, a.Occurrence.PeriodOwnerId).Should().Be((true, w.P1.Id));
        (b.Occurrence.PeriodOwnerFrozen, b.Occurrence.PeriodOwnerId).Should().Be((true, null));
        c.Occurrence.PeriodOwnerFrozen.Should().BeFalse();
        d.Occurrence.PeriodOwnerFrozen.Should().BeFalse();
        w.Entries(AuditEntity.Occurrence, AuditAction.Complete).First().After["periodOwnerId"].Should().Be(Id(w.P1.Id));
        w.Entries(AuditEntity.Occurrence, AuditAction.Complete).Skip(1).First().After["periodOwnerId"].Should().Be(AuditNull.Instance);
    }

    [Fact]
    public async Task Complete_anAlreadyFrozenOwnerStays()
    {
        var w = new OccurrenceWorld();
        w.Clock.Now = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
        var frozen = w.Seed(w.Weekly, "2026-09-14", w.P1, tweak: o => o with { PeriodOwnerId = w.P2.Id, PeriodOwnerFrozen = true });

        var view = (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), frozen.Id, new CompleteCommand(), Ct)).AsT0;

        view.Occurrence.PeriodOwnerId.Should().Be(w.P2.Id);
    }

    [Fact]
    public async Task Complete_aFailingAuditEntryRollsTheWholeCompletionBack()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P1);
        w.Audit.Failure = new PortError("audit down");

        var result = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct);

        result.AsT5.Message.Should().Be("audit down");
        w.Transactions.Aborts.Should().Be(1);
        w.Stored(occurrence).Status.Should().Be(OccurrenceStatus.Open);
        w.TaskStore.Items.Single(t => t.Id == w.Weekly.Id).LastCompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Complete_aConcurrentChangeBetweenReadAndWriteIsInvalidTransition()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P1);
        w.Occurrences.ConcurrentWriteBeforeNextUpdate = () =>
            w.Occurrences.Items[0] = w.Occurrences.Items[0] with { Status = OccurrenceStatus.Skipped };

        var result = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct);

        result.AsT3.Extensions.Should().Equal(new Dictionary<string, object?> { ["status"] = "changed", ["action"] = "complete" });
        w.Entries(AuditEntity.Occurrence).Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_aTransactionConflictAfterTheAttemptsIsAConflict()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P1);
        w.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "busy");

        var result = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct);

        result.AsT3.Code.Should().Be("write_conflict");
    }

    [Fact]
    public async Task Complete_withoutSettingsIsSettingsMissing()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P1);
        w.SettingsStore.Document = null;

        var result = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct);

        result.IsT4.Should().BeTrue();
    }

    // ---- uncomplete

    [Fact]
    public async Task Uncomplete_afterSkipThenDoneRestoresSkippedAndLeavesTheHistory()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-09-24");
        (await w.Service.SkipAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, "geen tijd", Ct)).IsT0.Should().BeTrue();
        (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(TakeOver: true), Ct)).AsT0
            .Occurrence.StatusBeforeCompletion.Should().Be(OccurrenceStatus.Skipped);

        var view = (await w.Service.UncompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Ct)).AsT0;

        view.Occurrence.Should().Match<Occurrence>(o =>
            o.Status == OccurrenceStatus.Skipped && o.SkipReason == "geen tijd" && o.StatusBeforeCompletion == null && o.CompletedAt == null && o.CompletedBy == null);
        w.Entries(AuditEntity.Occurrence).Select(e => e.Action).Should().Equal(AuditAction.Skip, AuditAction.Complete, AuditAction.Uncomplete);
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Uncomplete).Single();
        ((AuditObject)entry.Meta!["occurrence"]!)["taskNameSnapshot"].Should().Be(new AuditString("Wastafel"));
    }

    [Fact]
    public async Task Uncomplete_clearsThePointsSnapshotAsAnExplicitNullInTheHistory()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P1);
        (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();

        var view = (await w.Service.UncompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Ct)).AsT0;

        view.Occurrence.PointsSnapshot.Should().BeNull();
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Uncomplete).Single();
        entry.Before["pointsSnapshot"].Should().Be(new AuditInteger(30));
        entry.After["pointsSnapshot"].Should().Be(AuditNull.Instance);
    }

    [Fact]
    public async Task Uncomplete_keepsLastCompletedAtCorrectAcrossASequence()
    {
        var w = new OccurrenceWorld();
        var a = w.Seed(w.Weekly, "2026-10-12", w.P1);
        var b = w.Seed(w.Weekly, "2026-10-19", w.P1);
        var actor = OccurrenceWorld.Actor(w.P1);
        var first = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);
        LastCompleted().Should().BeNull();

        w.Clock.Now = first;
        (await w.Service.CompleteAsync(actor, a.Id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();
        w.Clock.Now = second;
        (await w.Service.CompleteAsync(actor, b.Id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();
        LastCompleted().Should().Be(second);

        (await w.Service.UncompleteAsync(actor, b.Id, Ct)).IsT0.Should().BeTrue();
        LastCompleted().Should().Be(first);
        (await w.Service.UncompleteAsync(actor, a.Id, Ct)).IsT0.Should().BeTrue();
        LastCompleted().Should().BeNull();

        var last = w.Entries(AuditEntity.Task, AuditAction.Update).Last();
        last.Before["lastCompletedAt"].Should().Be(new AuditInstant(first));
        last.After["lastCompletedAt"].Should().Be(AuditNull.Instance);
        DateTimeOffset? LastCompleted() => w.TaskStore.Items.Single(t => t.Id == w.Weekly.Id).LastCompletedAt;
    }

    [Fact]
    public async Task Uncomplete_anOpenOccurrenceIsInvalidTransition()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-10-26", w.P1);

        var result = await w.Service.UncompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Ct);

        result.AsT3.Extensions.Should().Equal(new Dictionary<string, object?> { ["status"] = "open", ["action"] = "uncomplete" });
    }

    [Fact]
    public async Task Uncomplete_recordedWorkMustBeRetracted()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P1, OccurrenceStatus.Done, o => o with { RecordedDone = true, CompletedAt = OccurrenceWorld.Now, CompletedBy = w.P1.Id });

        var result = await w.Service.UncompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Ct);

        result.AsT3.Code.Should().Be("retract_required");
        w.Writes.Should().Be(0);
    }

    // ---- edit completion (admin correction)

    [Fact]
    public async Task EditCompletion_correctsDayMomentAndPersonAndTheTaskFollows()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-17", w.P1);
        (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();
        var completedAt = new DateTimeOffset(2026, 9, 20, 12, 30, 0, TimeSpan.Zero);
        var command = new EditCompletionCommand(new DateOnly(2026, 9, 18), completedAt, w.P2.Id);

        var view = (await w.Service.EditCompletionAsync(OccurrenceWorld.Actor(w.Admin), occurrence.Id, command, Ct)).AsT0;

        view.Date.Should().Be(new DateOnly(2026, 9, 18));
        (view.Occurrence.CompletedAt, view.Occurrence.CompletedBy, view.Occurrence.Status).Should().Be((completedAt, w.P2.Id, OccurrenceStatus.Done));
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Update).Single();
        entry.Meta!["correction"].Should().Be(new AuditString("completion"));
        entry.Before["completedBy"].Should().Be(Id(w.P1.Id));
        w.TaskStore.Items.Single(t => t.Id == w.Weekly.Id).LastCompletedAt.Should().Be(completedAt);
    }

    [Fact]
    public async Task EditCompletion_toADayInTheOtherCycleMovesTheOccurrenceToThatCycle()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-10-11", w.P1);
        (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();

        var view = (await w.Service.EditCompletionAsync(OccurrenceWorld.Actor(w.Admin), occurrence.Id, new EditCompletionCommand(new DateOnly(2026, 10, 12), OccurrenceWorld.Now, w.P1.Id), Ct)).AsT0;

        view.Occurrence.CycleId.Should().Be(w.Cycle1.Id);
        view.CycleIndex.Should().Be(1);
    }

    [Fact]
    public async Task EditCompletion_aDayThatIsNotGeneratedIsRefusedWithTheDate()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-17", w.P1);
        (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();

        var result = await w.Service.EditCompletionAsync(OccurrenceWorld.Actor(w.Admin), occurrence.Id, new EditCompletionCommand(new DateOnly(2026, 12, 1), OccurrenceWorld.Now, w.P1.Id), Ct);

        result.AsT3.Code.Should().Be("cycle_not_generated");
        result.AsT3.Extensions.Should().Equal(new Dictionary<string, object?> { ["date"] = "2026-12-01" });
    }

    [Fact]
    public async Task EditCompletion_needsADoneOccurrenceAndAnExistingPerson()
    {
        var w = new OccurrenceWorld();
        var open = w.Seed(w.Weekly, "2026-09-17", w.P1);
        var done = w.Seed(w.Weekly, "2026-09-18", w.P1, OccurrenceStatus.Done, o => o with { CompletedAt = OccurrenceWorld.Now, CompletedBy = w.P1.Id });

        var notDone = await w.Service.EditCompletionAsync(OccurrenceWorld.Actor(w.Admin), open.Id, new EditCompletionCommand(new DateOnly(2026, 9, 17), OccurrenceWorld.Now, w.P1.Id), Ct);
        var unknown = await w.Service.EditCompletionAsync(OccurrenceWorld.Actor(w.Admin), done.Id, new EditCompletionCommand(new DateOnly(2026, 9, 18), OccurrenceWorld.Now, "0123456789abcdef01234567"), Ct);
        var malformed = await w.Service.EditCompletionAsync(OccurrenceWorld.Actor(w.Admin), done.Id, new EditCompletionCommand(new DateOnly(2026, 9, 18), OccurrenceWorld.Now, "x"), Ct);

        notDone.AsT3.Extensions.Should().Equal(new Dictionary<string, object?> { ["status"] = "open", ["action"] = "edit completion of" });
        unknown.AsT2.Errors["completedBy"].Should().Equal("unknown_user");
        malformed.AsT2.Errors["completedBy"].Should().Equal("invalid_object_id");
    }

    [Fact]
    public async Task EditCompletion_thatChangesNothingWritesAndAuditsNothing()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-17", w.P1);
        (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();
        var writes = w.Writes;

        var view = (await w.Service.EditCompletionAsync(OccurrenceWorld.Actor(w.Admin), occurrence.Id, new EditCompletionCommand(new DateOnly(2026, 9, 17), OccurrenceWorld.Now, w.P1.Id), Ct)).AsT0;

        view.Occurrence.Status.Should().Be(OccurrenceStatus.Done);
        w.Writes.Should().Be(writes);
    }

    [Fact]
    public async Task EditCompletion_aSubMillisecondDifferenceIsNoChange()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-17", w.P1);
        (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();
        var writes = w.Writes;

        var view = (await w.Service.EditCompletionAsync(OccurrenceWorld.Actor(w.Admin), occurrence.Id, new EditCompletionCommand(new DateOnly(2026, 9, 17), OccurrenceWorld.Now.AddTicks(1234), w.P1.Id), Ct)).AsT0;

        view.Occurrence.CompletedAt.Should().Be(OccurrenceWorld.Now);
        w.Writes.Should().Be(writes);
    }

    [Fact]
    public async Task EditCompletionAndReschedule_aDayAtTheEdgeOfTheCalendarIsAValidationError()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-17", w.P1, OccurrenceStatus.Done, o => o with { CompletedAt = OccurrenceWorld.Now, CompletedBy = w.P1.Id });

        var edit = await w.Service.EditCompletionAsync(OccurrenceWorld.Actor(w.Admin), occurrence.Id, new EditCompletionCommand(DateOnly.MinValue, OccurrenceWorld.Now, w.P1.Id), Ct);
        var move = await w.Service.RescheduleAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, DateOnly.MaxValue, Ct);

        edit.AsT2.Errors["date"].Should().Equal("out_of_range");
        move.AsT2.Errors["date"].Should().Equal("out_of_range");
    }

    // ---- delete (admin correction)

    [Fact]
    public async Task DeleteCompleted_removesItAuditsTheFieldsAndLastCompletedAtFallsBack()
    {
        var w = new OccurrenceWorld();
        var earlier = w.Seed(w.Weekly, "2026-09-15", w.P1, OccurrenceStatus.Done, o => o with { CompletedAt = new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero), CompletedBy = w.P1.Id });
        w.TaskStore.Items[0] = w.TaskStore.Items[0] with { LastCompletedAt = earlier.CompletedAt };
        var occurrence = w.Seed(w.Weekly, "2026-09-17", w.P1);
        (await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();
        w.TaskStore.Items.Single(t => t.Id == w.Weekly.Id).LastCompletedAt.Should().Be(OccurrenceWorld.Now);

        var result = await w.Service.DeleteCompletedAsync(OccurrenceWorld.Actor(w.Admin), occurrence.Id, Ct);

        result.IsT0.Should().BeTrue();
        w.Occurrences.Items.Should().NotContain(o => o.Id == occurrence.Id);
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Delete).Single();
        entry.Meta!["correction"].Should().Be(new AuditString("completion"));
        entry.Before["status"].Should().Be(new AuditString("done"));
        entry.After.Count.Should().Be(0);
        w.TaskStore.Items.Single(t => t.Id == w.Weekly.Id).LastCompletedAt.Should().Be(earlier.CompletedAt);
    }

    [Fact]
    public async Task DeleteCompleted_onlyDoneWorkAndOnlyWhatExists()
    {
        var w = new OccurrenceWorld();
        var open = w.Seed(w.Weekly, "2026-09-17", w.P1);

        var notDone = await w.Service.DeleteCompletedAsync(OccurrenceWorld.Actor(w.Admin), open.Id, Ct);
        var unknown = await w.Service.DeleteCompletedAsync(OccurrenceWorld.Actor(w.Admin), "0123456789abcdef01234567", Ct);
        var bad = await w.Service.DeleteCompletedAsync(OccurrenceWorld.Actor(w.Admin), "x", Ct);

        notDone.AsT3.Extensions.Should().Equal(new Dictionary<string, object?> { ["status"] = "open", ["action"] = "delete" });
        unknown.IsT1.Should().BeTrue();
        bad.AsT2.Errors["id"].Should().Equal("invalid_object_id");
        w.Occurrences.Items.Should().Contain(o => o.Id == open.Id);
    }
}
