using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Application.Tests.Occurrences;

/// <summary>
/// The one-off task scenarios of <c>one-off-occurrences.test.ts</c> (planned, done now, idempotent creation, retract, no task record) and the
/// retract rules of <c>adhoc-occurrences.test.ts</c> on the use cases with in-memory ports. Not here: the ledger effects (phase 4); the
/// task list, due list, AI input and plan activation scenarios, which are HTTP scenarios (integration tests).
/// </summary>
public sealed class AdhocOneOffTests
{
    private const string Key1 = "one-off-request-key-0001";
    private const string Key2 = "one-off-request-key-0002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateOnly Today = new(2026, 9, 16);

    private static DateOnly Day(string day) => DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture);

    private static Domain.Identity.Actor As(Domain.Users.User user) => OccurrenceWorld.Actor(user);

    // ---- planned

    [Fact]
    public async Task Plan_usesTheSnapshotsOnlyTrimsTheNameLeavesItUnassignedAndCreatesNoTask()
    {
        var w = new OccurrenceWorld();
        var tasks = w.TaskStore.Items.Count;

        var result = (await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("  Gordijnen ophangen ", w.Room.Id, 40, Day("2026-09-19")), Ct)).AsT0;

        result.Created.Should().BeTrue();
        var o = result.View.Occurrence;
        (o.TaskId, o.AssigneeId, o.Status, o.Origin, o.RecordedDone, o.RequestId, o.PlanId).Should().Be((null, null, OccurrenceStatus.Open, OccurrenceOrigin.Adhoc, false, null, null));
        (o.TaskNameSnapshot, o.RoomIdSnapshot, o.RoomNameSnapshot, o.DurationMinutesSnapshot).Should().Be(("Gordijnen ophangen", w.Room.Id, "Badkamer", 40));
        result.Warnings.Should().BeEmpty();
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Create).Should().ContainSingle().Subject;
        entry.Actor.Source.Should().Be(AuditSource.Ui);
        entry.Meta!["kind"].Should().Be(new AuditString("one_off"));
        entry.Meta["recordedDone"].Should().Be(new AuditBool(false));
        entry.After["taskId"].Should().Be(AuditNull.Instance);
        entry.After["taskNameSnapshot"].Should().Be(new AuditString("Gordijnen ophangen"));
        w.TaskStore.Items.Count.Should().Be(tasks);
    }

    [Fact]
    public async Task Plan_aMissingRoomIsStoredAsNullSnapshotsAndAnAssigneeOrAnyoneIsAccepted()
    {
        var w = new OccurrenceWorld();

        var named = (await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Kast ophalen", null, 25, Day("2026-09-20"), new AssigneeChoice(w.P2.Id)), Ct)).AsT0;
        var anyone = (await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Kast ophalen", null, 25, Day("2026-09-20"), new AssigneeChoice(null)), Ct)).AsT0;

        (named.View.Occurrence.RoomIdSnapshot, named.View.Occurrence.RoomNameSnapshot, named.View.Occurrence.AssigneeId).Should().Be((null, null, w.P2.Id));
        anyone.View.Occurrence.AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task Plan_severalIdenticalOneOffTasksOnOneDayCoexistWithoutAWarning()
    {
        var w = new OccurrenceWorld();
        var command = new OneOffCommand("Dubbele klus", null, 5, Day("2026-09-21"));

        var first = (await w.Adhoc.CreateOneOffAsync(As(w.P1), command, Ct)).AsT0;
        var second = (await w.Adhoc.CreateOneOffAsync(As(w.P1), command, Ct)).AsT0;

        (first.Created, second.Created).Should().Be((true, true));
        second.Warnings.Should().BeEmpty();
        w.Occurrences.Items.Count(o => o.TaskNameSnapshot == "Dubbele klus").Should().Be(2);
    }

    [Fact]
    public async Task Plan_validatesRoomAssigneeCycleAndShapeWithoutWriting()
    {
        var w = new OccurrenceWorld();
        var inactive = new Domain.Rooms.Room("0000000000000000000000ab", "Zolder", 20, false, false, OccurrenceWorld.Now, OccurrenceWorld.Now);
        w.Rooms.Items.Add(inactive);
        var unknown = "0123456789abcdef01234567";
        var basis = new OneOffCommand("Klus", null, 10, Day("2026-09-22"));

        (await w.Adhoc.CreateOneOffAsync(As(w.P1), basis with { RoomId = inactive.Id }, Ct)).AsT1.Errors["roomId"].Should().Equal("inactive_room");
        (await w.Adhoc.CreateOneOffAsync(As(w.P1), basis with { RoomId = unknown }, Ct)).AsT1.Errors["roomId"].Should().Equal("unknown_room");
        (await w.Adhoc.CreateOneOffAsync(As(w.P1), basis with { Assignee = new AssigneeChoice(unknown) }, Ct)).AsT1.Errors["assigneeId"].Should().Equal("unknown_user");
        foreach (var bad in new[]
        {
            basis with { Name = "   " },
            basis with { Name = new string('x', 121) },
            basis with { DurationMinutes = 0 },
            basis with { RequestId = "short" },
            basis with { Points = 1001 },
        })
        {
            (await w.Adhoc.CreateOneOffAsync(As(w.P1), bad, Ct)).IsT1.Should().BeTrue();
        }

        var notGenerated = await w.Adhoc.CreateOneOffAsync(As(w.P1), basis with { Date = Day("2026-12-01") }, Ct);

        notGenerated.AsT2.Code.Should().Be("cycle_not_generated");
        notGenerated.AsT2.Extensions!["date"].Should().Be("2026-12-01");
        w.Writes.Should().Be(0);
    }

    // ---- points (ADR-0011)

    [Fact]
    public async Task Points_theChosenValueIsStoredAndRecordedWorkSnapshotsIt()
    {
        var w = new OccurrenceWorld();

        var planned = (await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Klus", null, 90, Day("2026-09-22"), Points: 12), Ct)).AsT0;
        var recorded = (await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Klus", null, 90, Today, Done: true, Points: 12), Ct)).AsT0;

        (planned.View.Occurrence.PointsOverride, planned.View.Occurrence.PointsSnapshot).Should().Be((12, null));
        (recorded.View.Occurrence.PointsOverride, recorded.View.Occurrence.PointsSnapshot).Should().Be((12, 12));
    }

    [Fact]
    public async Task Points_withoutAChoiceRecordedWorkTakesOnePointPerMinuteAndStoresNoOverride()
    {
        var w = new OccurrenceWorld();

        var recorded = (await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Zolder opruimen", null, 90, Today, Done: true), Ct)).AsT0;

        (recorded.View.Occurrence.PointsOverride, recorded.View.Occurrence.PointsSnapshot).Should().Be((null, 90));
    }

    [Fact]
    public async Task Points_zeroIsAChoiceNotTheDefault()
    {
        var w = new OccurrenceWorld();

        var recorded = (await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Gratis klus", null, 30, Today, Done: true, Points: 0), Ct)).AsT0;

        (recorded.View.Occurrence.PointsOverride, recorded.View.Occurrence.PointsSnapshot).Should().Be((0, 0));
    }

    // ---- done now

    [Fact]
    public async Task Record_oneDoneDocumentForTheActorWithoutTouchingAnyTask()
    {
        var w = new OccurrenceWorld();
        var taskWrites = w.TaskStore.LastCompletedWrites;

        var result = (await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Zolder opruimen", w.Room.Id, 90, Today, Done: true, RequestId: Key1), Ct)).AsT0;

        var o = result.View.Occurrence;
        (o.TaskId, o.Status, o.RecordedDone, o.RequestId, o.StatusBeforeCompletion).Should().Be((null, OccurrenceStatus.Done, true, Key1, null));
        (o.CompletedAt, o.CompletedBy, o.AssigneeId).Should().Be((OccurrenceWorld.Now, w.P1.Id, w.P1.Id));
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Create).Single();
        entry.Meta!["kind"].Should().Be(new AuditString("one_off"));
        entry.Meta["recordedDone"].Should().Be(new AuditBool(true));
        entry.Meta["requestId"].Should().Be(new AuditString(Key1));
        entry.After["status"].Should().Be(new AuditString("done"));
        entry.After["recordedDone"].Should().Be(new AuditBool(true));
        w.TaskStore.LastCompletedWrites.Should().Be(taskWrites);
        w.Entries(AuditEntity.Task).Should().BeEmpty();
    }

    [Fact]
    public async Task Record_forAnExplicitPersonAndItRequiresTodayAndAPerson()
    {
        var w = new OccurrenceWorld();

        var explicitPerson = (await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Boekenkast", null, 15, Today, new AssigneeChoice(w.P2.Id), true), Ct)).AsT0;
        var writes = w.Writes;
        var future = await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Morgen", null, 15, Day("2026-09-17"), Done: true), Ct);
        var nobody = await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Niemand", null, 15, Today, new AssigneeChoice(null), true), Ct);

        (explicitPerson.View.Occurrence.CompletedBy, explicitPerson.View.Occurrence.AssigneeId).Should().Be((w.P2.Id, w.P2.Id));
        future.AsT1.Errors["date"].Should().Equal("done_requires_today");
        nobody.AsT1.Errors["assigneeId"].Should().Equal("done_requires_person");
        w.Writes.Should().Be(writes);
    }

    // ---- idempotent creation

    [Fact]
    public async Task Idempotent_aRepeatedRequestReplaysTheStoredRecordWithOneDocumentOneEntryAndNoWrites()
    {
        var w = new OccurrenceWorld();
        var command = new OneOffCommand("Fiets repareren", null, 30, Today, Done: true, RequestId: Key1);
        var first = (await w.Adhoc.CreateOneOffAsync(As(w.P1), command, Ct)).AsT0;
        var writes = w.Writes;

        var replay = (await w.Adhoc.CreateOneOffAsync(As(w.P1), command, Ct)).AsT0;

        replay.Created.Should().BeFalse();
        replay.View.Occurrence.Id.Should().Be(first.View.Occurrence.Id);
        w.Writes.Should().Be(writes);
        w.Occurrences.Items.Count(o => o.RequestId == Key1).Should().Be(1);
        w.Entries(AuditEntity.Occurrence, AuditAction.Create).Should().ContainSingle();
    }

    [Fact]
    public async Task Idempotent_aReusedKeyForADifferentRequestIsRefusedWithoutWriting()
    {
        var w = new OccurrenceWorld();
        var body = new OneOffCommand("Schuur leegmaken", null, 30, Today, RequestId: Key1);
        (await w.Adhoc.CreateOneOffAsync(As(w.P1), body, Ct)).IsT0.Should().BeTrue();
        var writes = w.Writes;

        var differentName = await w.Adhoc.CreateOneOffAsync(As(w.P1), body with { Name = "Andere klus" }, Ct);
        var differentDate = await w.Adhoc.CreateOneOffAsync(As(w.P1), body with { Date = Day("2026-09-17") }, Ct);
        var differentDone = await w.Adhoc.CreateOneOffAsync(As(w.P1), body with { Done = true }, Ct);
        var asExtra = await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today, RequestId: Key1), Ct);

        foreach (var code in new[] { differentName.AsT2.Code, differentDate.AsT2.Code, differentDone.AsT2.Code, asExtra.AsT2.Code })
        {
            code.Should().Be("idempotency_key_conflict");
        }

        w.Writes.Should().Be(writes);
        w.Occurrences.Items.Count(o => o.RequestId == Key1).Should().Be(1);
    }

    [Fact]
    public async Task Idempotent_theSameKeyForAnExtraExecutionAfterAOneOffIsAConflictToo()
    {
        var w = new OccurrenceWorld();
        (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today, RequestId: Key2), Ct)).IsT0.Should().BeTrue();

        var asOneOff = await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Badkamer schoonmaken", null, 30, Today, RequestId: Key2), Ct);

        asOneOff.AsT2.Code.Should().Be("idempotency_key_conflict");
    }

    [Fact]
    public async Task Race_theSameOneOffThatLostTheKeyReplaysTheWinnersRecord()
    {
        var w = new OccurrenceWorld();
        var command = new OneOffCommand("Fiets repareren", null, 30, Today, RequestId: Key1);
        w.Occurrences.ConcurrentInsertBeforeNextAdhoc = () =>
        {
            var date = OccurrenceWorld.At("2026-09-16");
            w.Occurrences.CommitOther(new NewAdhocOccurrence(null, w.Cycle0.Id, date, null, false, null, 30, "Fiets repareren", null, null, Key1, null, null, OccurrenceWorld.Now).ToOccurrence(w.Occurrences.NextId()));
        };

        var result = (await w.Adhoc.CreateOneOffAsync(As(w.P1), command, Ct)).AsT0;

        result.Created.Should().BeFalse();
        w.Occurrences.Items.Count(o => o.RequestId == Key1).Should().Be(1);
    }

    // ---- retract

    [Fact]
    public async Task Retract_deletesRecordedOneOffWorkWithAnAuditedRetractASecondRetractIsGoneAndUncompleteIsRefused()
    {
        var w = new OccurrenceWorld();
        var created = (await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Tuinhuis verven", null, 60, Today, Done: true, RequestId: Key1), Ct)).AsT0;
        var id = created.View.Occurrence.Id;

        var blocked = await w.Service.UncompleteAsync(As(w.P1), id, Ct);
        blocked.AsT3.Code.Should().Be("retract_required");

        (await w.Adhoc.RetractAsync(As(w.P1), id, Ct)).IsT0.Should().BeTrue();

        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Delete).Should().ContainSingle().Subject;
        entry.Meta!["reason"].Should().Be(new AuditString("retract"));
        entry.Before["taskId"].Should().Be(AuditNull.Instance);
        entry.Before["taskNameSnapshot"].Should().Be(new AuditString("Tuinhuis verven"));
        w.Occurrences.Items.Should().NotContain(o => o.Id == id);

        var writes = w.Writes;
        (await w.Adhoc.RetractAsync(As(w.P1), id, Ct)).IsT1.Should().BeTrue();
        w.Writes.Should().Be(writes);
    }

    [Fact]
    public async Task Retract_aPlannedOneOffCanBeCompletedAndSkippedLikeAnyOccurrenceAndIsNotRetractable()
    {
        var w = new OccurrenceWorld();
        var planned = (await w.Adhoc.CreateOneOffAsync(As(w.P1), new OneOffCommand("Planklus", null, 10, Day("2026-09-23")), Ct)).AsT0;
        var id = planned.View.Occurrence.Id;

        var done = (await w.Service.CompleteAsync(As(w.P1), id, new CompleteCommand(), Ct)).AsT0;
        (done.Occurrence.TaskId, done.Occurrence.Status, done.Occurrence.RecordedDone, done.Occurrence.PointsSnapshot).Should().Be((null, OccurrenceStatus.Done, false, 10));
        (await w.Service.UncompleteAsync(As(w.P1), id, Ct)).IsT0.Should().BeTrue();
        (await w.Adhoc.RetractAsync(As(w.P1), id, Ct)).AsT3.Code.Should().Be("not_retractable");
        (await w.Service.SkipAsync(As(w.P1), id, null, Ct)).IsT0.Should().BeTrue();
    }

    [Fact]
    public async Task Retract_refusesWorkThatWasNotRecordedAsDoneAndAnUnknownOrMalformedId()
    {
        var w = new OccurrenceWorld();
        var generated = w.Seed(w.Weekly, "2026-09-16", w.P1);
        (await w.Service.CompleteAsync(As(w.P1), generated.Id, new CompleteCommand(), Ct)).IsT0.Should().BeTrue();
        var writes = w.Writes;

        (await w.Adhoc.RetractAsync(As(w.P1), generated.Id, Ct)).AsT3.Code.Should().Be("not_retractable");
        (await w.Adhoc.RetractAsync(As(w.P1), "0123456789abcdef01234567", Ct)).IsT1.Should().BeTrue();
        (await w.Adhoc.RetractAsync(As(w.P1), "nope", Ct)).AsT2.Errors["id"].Should().Equal("invalid_object_id");

        w.Writes.Should().Be(writes);
    }

    [Fact]
    public async Task Retract_onlyWorkOfTodayAHistoricRecordIsRefusedAndStays()
    {
        var w = new OccurrenceWorld();
        var recorded = (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today, Done: true, RequestId: Key1), Ct)).AsT0;
        var id = recorded.View.Occurrence.Id;

        // The next day it is history: retracting is an undo of today's work, and older completions need an administrator.
        w.Clock.Now = new DateTimeOffset(2026, 9, 17, 6, 0, 0, TimeSpan.Zero);
        var writes = w.Writes;
        var refused = await w.Adhoc.RetractAsync(As(w.P2), id, Ct);

        refused.AsT3.Code.Should().Be("retract_not_today");
        w.Writes.Should().Be(writes);
        w.Occurrences.Items.Should().Contain(o => o.Id == id);
        w.TaskStore.Items.Single(t => t.Id == w.Weekly.Id).LastCompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Retract_aConcurrentRetractThatWasFirstIsTheSameAnswerAsASecondRetract()
    {
        var w = new OccurrenceWorld();
        var recorded = (await w.Adhoc.CreateExtraAsync(As(w.P1), new ExtraExecutionCommand(w.Weekly.Id, Today, Done: true), Ct)).AsT0;
        var id = recorded.View.Occurrence.Id;
        // The guarded delete finds nothing although the read saw the record: another retract was first.
        w.Occurrences.ConcurrentDeleteBeforeNextRetract = () => w.Occurrences.Items.RemoveAll(o => o.Id == id);
        var before = w.Audit.Entries.Count;

        var result = await w.Adhoc.RetractAsync(As(w.P1), id, Ct);

        result.IsT1.Should().BeTrue();
        w.Audit.Entries.Count.Should().Be(before);
    }
}
