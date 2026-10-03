using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Application.Tests.Occurrences;

/// <summary>
/// The extra executions of <c>adhoc-occurrences.test.ts</c> (planned extra, done now, idempotent creation) on the use case with in-memory
/// ports. Not here: the points ledger entry that follows recorded work and its retract (phase 4); the <c>pointsSnapshot</c> is asserted.
/// </summary>
public sealed class AdhocExtraExecutionTests
{
    private const string Key1 = "extra-execution-key-0001";
    private const string Key2 = "extra-execution-key-0002";
    private const string Key3 = "extra-execution-key-0003";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateOnly Today = new(2026, 9, 16);

    private static DateOnly Day(string day) => DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture);

    private static Domain.Identity.Actor As(Domain.Users.User user) => OccurrenceWorld.Actor(user);

    // ---- planned extra

    [Fact]
    public async Task Plan_usesTheDefaultAssigneeOfTheTaskAndWritesOneCreateEntryFromTheUi()
    {
        var w = new OccurrenceWorld();
        var ramen = w.NewTask("Ramen lappen", 60);
        w.TaskStore.Items[w.TaskStore.Items.FindIndex(t => t.Id == ramen.Id)] = ramen with { DefaultAssigneeId = w.P2.Id };

        var result = (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(ramen.Id, Day("2026-09-19")), Ct)).AsT0;

        result.Created.Should().BeTrue();
        var o = result.View.Occurrence;
        (o.TaskId, o.AssigneeId, o.Status, o.Origin, o.RecordedDone, o.RequestId, o.PlanId).Should().Be((ramen.Id, w.P2.Id, OccurrenceStatus.Open, OccurrenceOrigin.Adhoc, false, null, null));
        (o.TaskNameSnapshot, o.RoomNameSnapshot, o.DurationMinutesSnapshot).Should().Be(("Ramen lappen", "Badkamer", 60));
        (result.View.Date, result.View.PlannedDate, result.View.IsOverdue, result.View.MovedFrom).Should().Be((Day("2026-09-19"), Day("2026-09-19"), false, null));
        result.Warnings.Should().BeEmpty();
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Create).Should().ContainSingle().Subject;
        entry.Actor.Should().Be(new AuditActor(w.P1.Id, AuditSource.Ui));
        entry.Meta!["origin"].Should().Be(new AuditString("adhoc"));
        entry.Meta["kind"].Should().Be(new AuditString("extra"));
        entry.Meta["recordedDone"].Should().Be(new AuditBool(false));
        entry.Meta["requestId"].Should().Be(AuditNull.Instance);
    }

    [Fact]
    public async Task Plan_anExplicitAnyoneStaysUnassigned()
    {
        var w = new OccurrenceWorld();
        var ramen = w.NewTask("Ramen lappen", 60);
        w.TaskStore.Items[w.TaskStore.Items.FindIndex(t => t.Id == ramen.Id)] = ramen with { DefaultAssigneeId = w.P2.Id };

        var result = (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(ramen.Id, Day("2026-09-20"), new AssigneeChoice(null)), Ct)).AsT0;

        result.View.Occurrence.AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task Plan_warnsWhenTheTaskIsAlreadyPlannedThatDayButStillCreatesTheSecondOccurrence()
    {
        var w = new OccurrenceWorld();
        w.Seed(w.Weekly, "2026-09-19");

        var result = (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-19")), Ct)).AsT0;

        result.Created.Should().BeTrue();
        var warning = result.Warnings.Should().ContainSingle().Subject;
        warning.Code.Should().Be("task_already_planned");
        warning.Details.Should().BeEquivalentTo(new Dictionary<string, object?> { ["taskId"] = w.Weekly.Id, ["date"] = "2026-09-19" });
        w.Occurrences.Items.Count(o => o.TaskId == w.Weekly.Id).Should().Be(2);
    }

    [Fact]
    public async Task Plan_onlyWithinGeneratedCycles()
    {
        var w = new OccurrenceWorld();

        var result = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-12-01")), Ct);

        result.AsT2.Code.Should().Be("cycle_not_generated");
        result.AsT2.Extensions!["date"].Should().Be("2026-12-01");
        w.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Plan_aPlannedExtraThatIsCompletedLaterStillUncompletesBackToOpenAndIsNotRetractable()
    {
        var w = new OccurrenceWorld();
        var created = (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-21"), new AssigneeChoice(w.P1.Id)), Ct)).AsT0;
        var id = created.View.Occurrence.Id;

        (await w.Service.CompleteAsync(As(w.P1), id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();
        var open = (await w.Service.UncompleteAsync(As(w.P1), id, Ct)).AsT0;

        (open.Occurrence.Status, open.Occurrence.RecordedDone, open.Occurrence.CompletedAt).Should().Be((OccurrenceStatus.Open, false, null));
        (await w.Adhoc.RetractAsync(As(w.P1), id, Ct)).AsT3.Code.Should().Be("not_retractable");
    }

    [Fact]
    public async Task Plan_validatesTaskAssigneeDateAndRequestKey()
    {
        var w = new OccurrenceWorld();
        var unknown = "0123456789abcdef01234567";

        (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(unknown, Day("2026-09-22")), Ct)).AsT1.Errors["taskId"].Should().Equal("unknown_task");
        (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-22"), new AssigneeChoice(unknown)), Ct)).AsT1.Errors["assigneeId"].Should().Equal("unknown_user");
        (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand("nope", Day("2026-09-22")), Ct)).AsT1.Errors["taskId"].Should().Equal("invalid_object_id");
        (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-22"), RequestId: "short"), Ct)).AsT1.Errors["requestId"].Should().Equal("invalid_request_key");
        w.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Plan_anInactiveAssigneeIsRefused()
    {
        var w = new OccurrenceWorld();
        var gone = w.People.Add("Weg", active: false);

        var result = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-22"), new AssigneeChoice(gone.Id)), Ct);

        result.AsT1.Errors["assigneeId"].Should().Equal("inactive_user");
        w.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Plan_anInactiveTaskIsRefused()
    {
        var w = new OccurrenceWorld();
        var task = w.NewTask("Tijdelijke taak", 10);
        w.TaskStore.Items[w.TaskStore.Items.FindIndex(t => t.Id == task.Id)] = task with { Active = false };

        var result = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(task.Id, Day("2026-09-22")), Ct);

        result.AsT1.Errors["taskId"].Should().Equal("inactive_task");
        w.Writes.Should().Be(0);
    }

    // ---- done now

    [Fact]
    public async Task Record_threeSameDayExtrasNextToTheGeneratedOnesAndLastCompletedAtFollowsTheNewest()
    {
        var w = new OccurrenceWorld();
        var generated = w.Seed(w.Weekly, "2026-09-16", w.P1);
        (await w.Service.CompleteAsync(As(w.P1), generated.Id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();

        var ids = new List<string>();
        foreach (var (hour, key) in new[] { (9, Key1), (10, Key2), (11, Key3) })
        {
            w.Clock.Now = new DateTimeOffset(2026, 9, 16, hour, 0, 0, TimeSpan.Zero);
            var created = (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today, Done: true, RequestId: key), Ct)).AsT0;
            created.Created.Should().BeTrue();
            ids.Add(created.View.Occurrence.Id);
        }

        var all = w.Occurrences.Items.Where(o => o.TaskId == w.Weekly.Id).ToList();
        all.Should().HaveCount(4);
        all.Should().OnlyContain(o => o.Status == OccurrenceStatus.Done);
        all.Count(o => o.Origin == OccurrenceOrigin.Adhoc).Should().Be(3);
        LastCompleted(w, w.Weekly).Should().Be(new DateTimeOffset(2026, 9, 16, 11, 0, 0, TimeSpan.Zero));

        // Retracting the newest restores the previous completion; a second retract is gone.
        (await w.Adhoc.RetractAsync(As(w.P1), ids[2], Ct)).IsT0.Should().BeTrue();
        var retract = w.Entries(AuditEntity.Occurrence, AuditAction.Delete).Single();
        retract.Meta!["reason"].Should().Be(new AuditString("retract"));
        LastCompleted(w, w.Weekly).Should().Be(new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero));
        w.Occurrences.Items.Count(o => o.TaskId == w.Weekly.Id).Should().Be(3);

        var writes = w.Writes;
        (await w.Adhoc.RetractAsync(As(w.P1), ids[2], Ct)).IsT1.Should().BeTrue();
        w.Writes.Should().Be(writes);

        // Uncomplete would leave an open record behind, so recorded work must be retracted instead.
        var blocked = await w.Service.UncompleteAsync(As(w.P1), ids[1], Ct);
        blocked.AsT3.Code.Should().Be("retract_required");
        w.Writes.Should().Be(writes);

        (await w.Adhoc.RetractAsync(As(w.P1), ids[1], Ct)).IsT0.Should().BeTrue();
        (await w.Adhoc.RetractAsync(As(w.P1), ids[0], Ct)).IsT0.Should().BeTrue();
        // Only the generated completion remains.
        LastCompleted(w, w.Weekly).Should().Be(OccurrenceWorld.Now);
    }

    [Fact]
    public async Task Record_oneDoneDocumentForTheActorAuditedWithItsFinalFieldsAndLastCompletedAtFollows()
    {
        var w = new OccurrenceWorld();
        var ramen = w.NewTask("Ramen lappen", 60, 45);

        var result = (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(ramen.Id, Today, Done: true, RequestId: Key1), Ct)).AsT0;

        var o = result.View.Occurrence;
        (o.Status, o.Origin, o.RecordedDone, o.RequestId, o.StatusBeforeCompletion).Should().Be((OccurrenceStatus.Done, OccurrenceOrigin.Adhoc, true, Key1, null));
        (o.CompletedAt, o.CompletedBy, o.AssigneeId, o.PointsSnapshot).Should().Be((OccurrenceWorld.Now, w.P1.Id, w.P1.Id, 45));
        result.Warnings.Should().BeEmpty();
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Create).Single();
        entry.After["status"].Should().Be(new AuditString("done"));
        entry.After["recordedDone"].Should().Be(new AuditBool(true));
        entry.After["requestId"].Should().Be(new AuditString(Key1));
        entry.Meta!["recordedDone"].Should().Be(new AuditBool(true));
        entry.Meta["requestId"].Should().Be(new AuditString(Key1));
        LastCompleted(w, ramen).Should().Be(OccurrenceWorld.Now);
        // The task's own entry says which occurrence moved it.
        w.Entries(AuditEntity.Task, AuditAction.Update).Should().ContainSingle();
    }

    [Fact]
    public async Task Record_forAnExplicitPerson()
    {
        var w = new OccurrenceWorld();

        var result = (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today, new AssigneeChoice(w.P2.Id), true), Ct)).AsT0;

        (result.View.Occurrence.CompletedBy, result.View.Occurrence.AssigneeId).Should().Be((w.P2.Id, w.P2.Id));
    }

    [Fact]
    public async Task Record_requiresTodayAndAPersonAndWritesNothingOtherwise()
    {
        var w = new OccurrenceWorld();

        var future = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-17"), Done: true), Ct);
        var past = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-15"), Done: true), Ct);
        var nobody = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today, new AssigneeChoice(null), true), Ct);

        future.AsT1.Errors["date"].Should().Equal("done_requires_today");
        past.AsT1.Errors["date"].Should().Equal("done_requires_today");
        nobody.AsT1.Errors["assigneeId"].Should().Equal("done_requires_person");
        w.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Record_warnsAboutAPlannedOccurrenceAndLeavesItAloneAndAReplayDoesNotWarnAgain()
    {
        var w = new OccurrenceWorld();
        var task = w.NewTask("Planten water geven", 5);
        w.Seed(task, "2026-09-16");

        var first = (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(task.Id, Today, Done: true, RequestId: Key1), Ct)).AsT0;

        first.Created.Should().BeTrue();
        first.Warnings.Should().ContainSingle().Which.Code.Should().Be("task_already_planned");
        w.Occurrences.Items.Count(o => o.TaskId == task.Id && o.Status == OccurrenceStatus.Open).Should().Be(1);

        var replay = (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(task.Id, Today, Done: true, RequestId: Key1), Ct)).AsT0;
        (replay.Created, replay.Warnings).Should().Be((false, Array.Empty<OccurrenceWarning>()));
    }

    // ---- idempotent creation

    [Fact]
    public async Task Idempotent_aRepeatedRequestReplaysTheStoredRecordWithOneDocumentOneEntryAndNoWrites()
    {
        var w = new OccurrenceWorld();
        var command = new ExtraExecutionCommand(w.Weekly.Id, Today, Done: true, RequestId: Key1);
        var first = (await w.Adhoc.CreateExtraAsync(As(w.P1), command, Ct)).AsT0;
        w.Clock.Now = OccurrenceWorld.Now.AddMinutes(5);
        var writes = w.Writes;

        var replay = (await w.Adhoc.CreateExtraAsync(As(w.P1), command, Ct)).AsT0;

        replay.Created.Should().BeFalse();
        replay.View.Occurrence.Id.Should().Be(first.View.Occurrence.Id);
        w.Writes.Should().Be(writes);
        w.Occurrences.Items.Count(o => o.RequestId == Key1).Should().Be(1);
        w.Entries(AuditEntity.Occurrence, AuditAction.Create).Should().ContainSingle();
        LastCompleted(w, w.Weekly).Should().Be(OccurrenceWorld.Now);
    }

    [Fact]
    public async Task Idempotent_aReusedKeyForADifferentRequestIsRefusedWithoutWriting()
    {
        var w = new OccurrenceWorld();
        var other = w.NewTask("Stofzuigen trap", 10);
        (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today, RequestId: Key1), Ct)).IsT0.Should().BeTrue();
        var writes = w.Writes;

        var differentDate = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Day("2026-09-17"), RequestId: Key1), Ct);
        var differentTask = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(other.Id, Today, RequestId: Key1), Ct);
        var differentDone = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today, Done: true, RequestId: Key1), Ct);

        foreach (var result in new[] { differentDate, differentTask, differentDone })
        {
            result.AsT2.Code.Should().Be("idempotency_key_conflict");
        }

        w.Writes.Should().Be(writes);
        w.Occurrences.Items.Count(o => o.RequestId == Key1).Should().Be(1);
    }

    [Fact]
    public async Task Idempotent_theKeyCreatesTheRecordAgainAfterARetract()
    {
        var w = new OccurrenceWorld();
        var command = new ExtraExecutionCommand(w.Weekly.Id, Today, Done: true, RequestId: Key1);
        var first = (await w.Adhoc.CreateExtraAsync(As(w.P1), command, Ct)).AsT0;
        (await w.Adhoc.RetractAsync(As(w.P1), first.View.Occurrence.Id, Ct)).IsT0.Should().BeTrue();

        var again = (await w.Adhoc.CreateExtraAsync(As(w.P1), command, Ct)).AsT0;

        again.Created.Should().BeTrue();
        again.View.Occurrence.Id.Should().NotBe(first.View.Occurrence.Id);
    }

    [Fact]
    public async Task Idempotent_aReplayHappensBeforeEverythingElseSoItSurvivesALaterChangeOfTheTask()
    {
        var w = new OccurrenceWorld();
        var command = new ExtraExecutionCommand(w.Weekly.Id, Today, RequestId: Key1);
        (await w.Adhoc.CreateExtraAsync(As(w.P1), command, Ct)).IsT0.Should().BeTrue();
        w.TaskStore.Items[w.TaskStore.Items.FindIndex(t => t.Id == w.Weekly.Id)] = w.Weekly with { Active = false };

        var replay = (await w.Adhoc.CreateExtraAsync(As(w.P1), command, Ct)).AsT0;

        replay.Created.Should().BeFalse();
    }

    // ---- a lost race for the key

    [Fact]
    public async Task Race_theSameRequestThatLostTheKeyReplaysTheWinnersRecord()
    {
        var w = new OccurrenceWorld();
        var command = new ExtraExecutionCommand(w.Weekly.Id, Today, RequestId: Key1);
        w.Occurrences.ConcurrentInsertBeforeNextAdhoc = () => w.Occurrences.CommitOther(w.Winner(w.Weekly, "2026-09-16", Key1));

        var result = (await w.Adhoc.CreateExtraAsync(As(w.P1), command, Ct)).AsT0;

        result.Created.Should().BeFalse();
        w.Occurrences.Items.Count(o => o.RequestId == Key1).Should().Be(1);
        // The loser audits nothing: the winner wrote its own entry in its own transaction.
        w.Entries(AuditEntity.Occurrence, AuditAction.Create).Should().BeEmpty();
        w.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Race_aDifferentRequestThatLostTheKeyIsRefusedAsAConflict()
    {
        var w = new OccurrenceWorld();
        var winner = new ExtraExecutionCommand(w.Weekly.Id, Today, RequestId: Key1);
        w.Occurrences.ConcurrentInsertBeforeNextAdhoc = () => w.Occurrences.CommitOther(w.Winner(w.Weekly, "2026-09-16", Key1));

        var result = await w.Adhoc.CreateExtraAsync(As(w.P1), winner with { Date = Day("2026-09-17") }, Ct);

        result.AsT2.Code.Should().Be("idempotency_key_conflict");
        w.Occurrences.Items.Count(o => o.RequestId == Key1).Should().Be(1);
    }

    [Fact]
    public async Task Race_aKeyThatStaysTakenWithoutAVisibleRecordEndsAsAConflictInsteadOfLooping()
    {
        var w = new OccurrenceWorld();
        // Every attempt finds nothing and fails on the index: the record that holds the key is never visible.
        w.Occurrences.KeyAlwaysTaken = true;

        var result = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today, RequestId: Key1), Ct);

        result.AsT2.Code.Should().Be("idempotency_key_conflict");
        w.Occurrences.AdhocInsertAttempts.Should().Be(3);
        w.Transactions.Aborts.Should().Be(3);
        w.Writes.Should().Be(0);
    }

    // ---- failures

    [Fact]
    public async Task APortFailureOfTheStoreIsReportedAndNothingIsWritten()
    {
        var w = new OccurrenceWorld();
        w.Occurrences.Failure = new Domain.Errors.PortError("occurrences.failed: down");

        var result = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today), Ct);

        result.AsT4.Message.Should().Contain("occurrences.failed");
        w.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingSettingsAreReported()
    {
        var w = new OccurrenceWorld();
        w.SettingsStore.Document = null;

        var result = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today), Ct);

        result.IsT3.Should().BeTrue();
    }

    private static DateTimeOffset? LastCompleted(OccurrenceWorld w, HouseholdTask task) => w.TaskStore.Items.Single(t => t.Id == task.Id).LastCompletedAt;
}
