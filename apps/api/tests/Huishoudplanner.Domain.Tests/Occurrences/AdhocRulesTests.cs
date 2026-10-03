using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Domain.Tests.Occurrences;

/// <summary>The pure rules of the extra executions, one-off tasks and retract (slice 3.3): request shapes, recorded work, replay identity and the audit entries.</summary>
public sealed class AdhocRulesTests
{
    private const string Anna = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TaskId = "222222222222222222222222";
    private const string Key = "extra-execution-key-0001";

    private static readonly TimeZoneInfo Zone = DayKeys.FindZone("Europe/Amsterdam");
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 16);

    private static Occurrence Recorded(string? taskId = TaskId, string name = "Afwas", bool done = true, string? requestId = Key)
    {
        var date = DayKeys.FromDayKey(Today, Zone);
        return new Occurrence(
            "111111111111111111111111", taskId, "dddddddddddddddddddddddd", null, date, date, Anna,
            done ? OccurrenceStatus.Done : OccurrenceStatus.Open, null, done ? Now : null, done ? Anna : null, null, 20, name, null, null,
            OccurrenceOrigin.Adhoc, Now, Now, done, requestId, done ? 20 : null);
    }

    // ---- the request key and the shape of the requests

    [Theory]
    [InlineData("1234567890123456", true)]
    [InlineData("abc_DEF-1234567890", true)]
    [InlineData("short", false)]
    [InlineData("123456789012345", false)]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234", true)]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345", false)]
    [InlineData("1234567890123456\n", false)]
    [InlineData("with space 1234567890", false)]
    public void IsRequestKey_is16To64LettersDigitsUnderscoreAndHyphen(string key, bool expected)
    {
        AdhocRules.IsRequestKey(key).Should().Be(expected);
    }

    [Fact]
    public void ValidateExtra_acceptsAValidRequestAndNamesEveryProblemByField()
    {
        AdhocRules.Validate(new ExtraExecutionCommand(TaskId, Today, new AssigneeChoice(Anna), true, Key)).Should().BeNull();
        AdhocRules.Validate(new ExtraExecutionCommand(TaskId, Today, new AssigneeChoice(null))).Should().BeNull();

        var errors = AdhocRules.Validate(new ExtraExecutionCommand("nope", Today, new AssigneeChoice("nope"), false, "short"))!.Errors;

        errors["taskId"].Should().Equal("invalid_object_id");
        errors["assigneeId"].Should().Equal("invalid_object_id");
        errors["requestId"].Should().Equal("invalid_request_key");
        AdhocRules.Validate(new ExtraExecutionCommand(TaskId, new DateOnly(1, 1, 1)))!.Errors["date"].Should().Equal("out_of_range");
    }

    [Fact]
    public void ValidateOneOff_checksTheNameTrimmedTheDurationThePointsAndTheRoom()
    {
        AdhocRules.Validate(new OneOffCommand("  Gordijnen ophangen ", Anna, 40, Today, Points: 0)).Should().BeNull();
        AdhocRules.Validate(new OneOffCommand(new string('x', 120), null, 1, Today, Points: 1000)).Should().BeNull();

        AdhocRules.Validate(new OneOffCommand("   ", null, 10, Today))!.Errors.Should().ContainKey("name");
        AdhocRules.Validate(new OneOffCommand(new string('x', 121), null, 10, Today))!.Errors.Should().ContainKey("name");
        AdhocRules.Validate(new OneOffCommand("Klus", null, 0, Today))!.Errors.Should().ContainKey("durationMinutes");
        AdhocRules.Validate(new OneOffCommand("Klus", null, 10, Today, Points: 1001))!.Errors.Should().ContainKey("points");
        AdhocRules.Validate(new OneOffCommand("Klus", null, 10, Today, Points: -1))!.Errors.Should().ContainKey("points");
        AdhocRules.Validate(new OneOffCommand("Klus", "nope", 10, Today))!.Errors["roomId"].Should().Equal("invalid_object_id");
        AdhocRules.Validate(new OneOffCommand("Klus", null, 10, Today, RequestId: "short"))!.Errors["requestId"].Should().Equal("invalid_request_key");
    }

    // ---- recorded work

    [Fact]
    public void CheckRecordedWork_needsTodayAndAPerson()
    {
        AdhocRules.CheckRecordedWork(false, Today.AddDays(3), Today, new AssigneeChoice(null)).Should().BeNull();
        AdhocRules.CheckRecordedWork(true, Today, Today, null).Should().BeNull();
        AdhocRules.CheckRecordedWork(true, Today, Today, new AssigneeChoice(Anna)).Should().BeNull();
        AdhocRules.CheckRecordedWork(true, Today.AddDays(1), Today, null)!.Errors["date"].Should().Equal("done_requires_today");
        AdhocRules.CheckRecordedWork(true, Today.AddDays(-1), Today, null)!.Errors["date"].Should().Equal("done_requires_today");
        AdhocRules.CheckRecordedWork(true, Today, Today, new AssigneeChoice(null))!.Errors["assigneeId"].Should().Equal("done_requires_person");
    }

    [Theory]
    [InlineData(null, false, Anna, "fallback")]
    [InlineData(null, true, Anna, Anna)]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbb", true, Anna, "bbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbb", false, Anna, "bbbbbbbbbbbbbbbbbbbbbbbb")]
    public void ResolveAssignee_saidIsSaidElseTheActorWhenDoneElseTheFallback(string? said, bool done, string actor, string? expected)
    {
        var requested = said is null ? null : new AssigneeChoice(said);

        AdhocRules.ResolveAssignee(requested, done, actor, "fallback").Should().Be(expected);
    }

    [Fact]
    public void ResolveAssignee_anExplicitAnyoneStaysAnyone()
    {
        AdhocRules.ResolveAssignee(new AssigneeChoice(null), false, Anna, "fallback").Should().BeNull();
    }

    // ---- replay or conflict

    [Fact]
    public void IsReplay_theSameTaskDayAndDoneFlagIsTheSameRequest()
    {
        var stored = Recorded();

        AdhocRules.IsReplay(stored, new AdhocIdentity(TaskId, string.Empty, Today, true), Zone).Should().BeTrue();
        AdhocRules.IsReplay(stored, new AdhocIdentity(TaskId.ToUpperInvariant(), string.Empty, Today, true), Zone).Should().BeTrue();
        AdhocRules.IsReplay(stored, new AdhocIdentity(TaskId, string.Empty, Today.AddDays(1), true), Zone).Should().BeFalse();
        AdhocRules.IsReplay(stored, new AdhocIdentity("333333333333333333333333", string.Empty, Today, true), Zone).Should().BeFalse();
        AdhocRules.IsReplay(stored, new AdhocIdentity(TaskId, string.Empty, Today, false), Zone).Should().BeFalse();
    }

    [Fact]
    public void IsReplay_aOneOffTaskMatchesOnItsNameAndNeverOnAnExtraExecution()
    {
        var oneOff = Recorded(taskId: null, name: "Zolder opruimen");

        AdhocRules.IsReplay(oneOff, new AdhocIdentity(null, "Zolder opruimen", Today, true), Zone).Should().BeTrue();
        AdhocRules.IsReplay(oneOff, new AdhocIdentity(null, "Andere klus", Today, true), Zone).Should().BeFalse();
        AdhocRules.IsReplay(oneOff, new AdhocIdentity(TaskId, string.Empty, Today, true), Zone).Should().BeFalse();
        AdhocRules.IsReplay(Recorded(), new AdhocIdentity(null, "Afwas", Today, true), Zone).Should().BeFalse();
    }

    // ---- retract

    [Fact]
    public void IsRetractable_onlyRecordedAdhocWorkThatIsDone()
    {
        AdhocRules.IsRetractable(Recorded()).Should().BeTrue();
        AdhocRules.IsRetractable(Recorded(done: false)).Should().BeFalse();
        AdhocRules.IsRetractable(Recorded() with { Origin = OccurrenceOrigin.Generated }).Should().BeFalse();
        AdhocRules.IsRetractable(Recorded() with { RecordedDone = false }).Should().BeFalse();
    }

    // ---- the warning

    [Fact]
    public void AlreadyPlanned_carriesTheTaskAndTheDay()
    {
        var warning = AdhocRules.AlreadyPlanned(TaskId, Today);

        warning.Code.Should().Be("task_already_planned");
        warning.Details.Should().BeEquivalentTo(new Dictionary<string, object?> { ["taskId"] = TaskId, ["date"] = "2026-09-16" });
    }

    // ---- the draft and the audit entries

    [Fact]
    public void NewAdhocOccurrence_recordedWorkIsDoneForThePersonWithoutAPlan()
    {
        var date = DayKeys.FromDayKey(Today, Zone);
        var draft = new NewAdhocOccurrence(TaskId, "dddddddddddddddddddddddd", date, Anna, true, Now, 20, "Afwas", null, null, Key, 20, null, Now);

        var stored = draft.ToOccurrence("111111111111111111111111");

        (stored.Status, stored.CompletedBy, stored.CompletedAt, stored.RecordedDone, stored.Origin, stored.PlanId).Should().Be((OccurrenceStatus.Done, Anna, Now, true, OccurrenceOrigin.Adhoc, null));
        (stored.PlannedDate, stored.PointsSnapshot, stored.RequestId).Should().Be((date, 20, Key));
    }

    [Fact]
    public void NewAdhocOccurrence_plannedWorkIsOpenAndHasNoCompletion()
    {
        var date = DayKeys.FromDayKey(Today, Zone);
        var draft = new NewAdhocOccurrence(null, "dddddddddddddddddddddddd", date, null, false, null, 20, "Klus", null, null, null, null, 15, Now);

        var stored = draft.ToOccurrence("111111111111111111111111");

        (stored.Status, stored.CompletedBy, stored.CompletedAt, stored.RecordedDone, stored.TaskId, stored.PointsOverride).Should().Be((OccurrenceStatus.Open, null, null, false, null, 15));
    }

    [Fact]
    public void ForAdhocCreated_writesOneCreateEntryWithEveryFinalFieldAndTheNodeMeta()
    {
        var entry = OccurrenceAudit.ForAdhocCreated(new AuditActor(Anna, AuditSource.Ui), Recorded(), AdhocKind.Extra);

        entry.Action.Should().Be(AuditAction.Create);
        entry.Entity.Should().Be(AuditEntity.Occurrence);
        entry.Before.Properties.Should().BeEmpty();
        entry.After["status"].Should().Be(new AuditString("done"));
        entry.After["recordedDone"].Should().Be(new AuditBool(true));
        entry.After["requestId"].Should().Be(new AuditString(Key));
        entry.Meta!.Properties.Select(p => p.Key).Should().Equal("origin", "kind", "recordedDone", "requestId");
        entry.Meta["kind"].Should().Be(new AuditString("extra"));
    }

    [Fact]
    public void ForAdhocCreated_aPlannedOneOffHasExplicitFalseAndNullAndKindOneOff()
    {
        var planned = Recorded(taskId: null, name: "Gordijnen", done: false, requestId: null);

        var entry = OccurrenceAudit.ForAdhocCreated(AuditActor.System, planned, AdhocKind.OneOff);

        entry.After["recordedDone"].Should().Be(new AuditBool(false));
        entry.After["requestId"].Should().Be(AuditNull.Instance);
        entry.After["taskId"].Should().Be(AuditNull.Instance);
        entry.Meta!["kind"].Should().Be(new AuditString("one_off"));
        entry.Meta["recordedDone"].Should().Be(new AuditBool(false));
        entry.Meta["requestId"].Should().Be(AuditNull.Instance);
    }

    [Fact]
    public void ForRetracted_isADeleteThatKeepsTheRemovedFieldsWithTheRetractReason()
    {
        var entry = OccurrenceAudit.ForRetracted(AuditActor.System, Recorded());

        entry.Action.Should().Be(AuditAction.Delete);
        entry.After.Properties.Should().BeEmpty();
        entry.Before["taskNameSnapshot"].Should().Be(new AuditString("Afwas"));
        entry.Meta!.Properties.Should().ContainSingle().Which.Value.Should().Be(new AuditString("retract"));
        entry.Meta["reason"].Should().Be(new AuditString("retract"));
    }

    [Fact]
    public void ForRetracted_aKeylessRecordedExtraKeepsExplicitRequestIdNullAndRecordedDoneTrueInBefore()
    {
        var entry = OccurrenceAudit.ForRetracted(AuditActor.System, Recorded(requestId: null));

        entry.Before["requestId"].Should().Be(AuditNull.Instance);
        entry.Before["recordedDone"].Should().Be(new AuditBool(true));
    }

    [Fact]
    public void ForCorrectionDeleteAndForRemoved_anAdhocDocumentKeepsTheExplicitNullAndFalseNodeStores()
    {
        var planned = Recorded(done: false, requestId: null);

        var correction = OccurrenceAudit.ForCorrectionDelete(AuditActor.System, planned);
        var removed = OccurrenceAudit.ForRemoved(AuditActor.System, planned, "run", "444444444444444444444444", "plan_update");

        foreach (var entry in new[] { correction, removed })
        {
            entry.Before["requestId"].Should().Be(AuditNull.Instance);
            entry.Before["recordedDone"].Should().Be(new AuditBool(false));
        }
    }

    [Fact]
    public void ForCorrectionDelete_aGeneratedDocumentStillOmitsThem()
    {
        var generated = Recorded(done: false, requestId: null) with { Origin = OccurrenceOrigin.Generated };

        OccurrenceAudit.ForCorrectionDelete(AuditActor.System, generated).Before.Properties.Select(p => p.Key).Should().NotContain(["requestId", "recordedDone"]);
    }
}
