using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Tasks;
using Huishoudplanner.Domain.Users;

namespace Huishoudplanner.Domain.Tests.Occurrences;

/// <summary>The pure rules of the occurrence actions: the completion choice, the period owner freeze, the transitions with their history, the view and the cursor.</summary>
public sealed class OccurrenceActionsTests
{
    private const string Anna = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Bram = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Cleo = "cccccccccccccccccccccccc";
    private const string CycleId = "dddddddddddddddddddddddd";
    private const string OtherCycleId = "eeeeeeeeeeeeeeeeeeeeeeee";

    private static readonly TimeZoneInfo Zone = DayKeys.FindZone("Europe/Amsterdam");
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Anchor = new(2026, 9, 14);

    private static Occurrence Open(string day = "2026-09-16", string? assignee = Anna)
    {
        var date = DayKeys.FromDayKey(DayKeys.Parse(day), Zone);
        return new Occurrence(
            "111111111111111111111111", "222222222222222222222222", CycleId, "333333333333333333333333", date, date, assignee,
            OccurrenceStatus.Open, null, null, null, null, 30, "Badkamer", "444444444444444444444444", "Badkamer", OccurrenceOrigin.Generated, Now.AddDays(-2), Now.AddDays(-2));
    }

    // ---- the completion choice

    [Fact]
    public void Validate_bothChoicesAtOnceIsAConflictAndAMalformedIdIsInvalid()
    {
        OccurrenceRules.Validate(new CompleteCommand(Bram, TakeOver: true))!.Errors["completedBy"].Should().Equal("completion_choice_conflict");
        OccurrenceRules.Validate(new CompleteCommand("nope"))!.Errors["completedBy"].Should().Equal("invalid_object_id");
        OccurrenceRules.Validate(new CompleteCommand(Bram)).Should().BeNull();
        OccurrenceRules.Validate(new CompleteCommand(TakeOver: true)).Should().BeNull();
        OccurrenceRules.Validate(new CompleteCommand()).Should().BeNull();
    }

    [Theory]
    [InlineData(Bram, Anna, null, false, true)]
    [InlineData(Bram, Anna, Anna, false, false)]
    [InlineData(Bram, Anna, null, true, false)]
    [InlineData(Anna, Anna, null, false, false)]
    [InlineData(null, Anna, null, false, false)]
    public void NeedsCompletionChoice_onlyForWorkOfSomeoneElseWithoutAChoice(string? assignee, string actor, string? completedBy, bool takeOver, bool expected)
    {
        OccurrenceRules.NeedsCompletionChoice(Open(assignee: assignee), actor, new CompleteCommand(completedBy, takeOver)).Should().Be(expected);
    }

    [Fact]
    public void NormaliseSkipReason_trimsAndTurnsEmptyIntoNone()
    {
        OccurrenceRules.NormaliseSkipReason("  geen tijd ").Should().Be(("geen tijd", null));
        OccurrenceRules.NormaliseSkipReason("   ").Should().Be((null, null));
        OccurrenceRules.NormaliseSkipReason(null).Should().Be((null, null));
        OccurrenceRules.NormaliseSkipReason(new string('x', 500)).Error.Should().BeNull();
        OccurrenceRules.NormaliseSkipReason(new string('x', 501)).Error!.Errors.Should().ContainKey("reason");
    }

    // ---- the period owner (ADR-0012)

    [Fact]
    public void FreezePeriodOwner_remembersTheAssigneeOnceThePlannedWeekHasEnded()
    {
        var current = Open("2026-09-14", Anna);

        var changed = OccurrenceRules.FreezePeriodOwner(current, current with { AssigneeId = Bram }, Zone, new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero));

        (changed.PeriodOwnerFrozen, changed.PeriodOwnerId, changed.AssigneeId).Should().Be((true, Anna, Bram));
    }

    [Fact]
    public void FreezePeriodOwner_freezesAnUnassignedOccurrenceAsNobody()
    {
        var current = Open("2026-09-14", null);

        var changed = OccurrenceRules.FreezePeriodOwner(current, current with { AssigneeId = Bram }, Zone, new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero));

        (changed.PeriodOwnerFrozen, changed.PeriodOwnerId, changed.HasPeriodOwner).Should().Be((true, null, true));
    }

    [Theory]
    [InlineData("2026-09-20T21:30:00Z", false)] // Sunday 23:30 in Amsterdam: the last day of the week is not over yet
    [InlineData("2026-09-20T22:30:00Z", true)] // Monday 00:30 in Amsterdam: the week has ended
    public void FreezePeriodOwner_followsTheHouseholdDayNotUtc(string instant, bool frozen)
    {
        var current = Open("2026-09-14", Anna);
        var now = DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

        var changed = OccurrenceRules.FreezePeriodOwner(current, current with { AssigneeId = Bram }, Zone, now);

        changed.PeriodOwnerFrozen.Should().Be(frozen);
    }

    [Fact]
    public void FreezePeriodOwner_leavesRecordedWorkAndAnAlreadyFrozenOwnerAlone()
    {
        var late = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
        var recorded = Open("2026-09-14", Anna) with { RecordedDone = true };
        var frozen = Open("2026-09-14", Anna) with { PeriodOwnerId = Cleo, PeriodOwnerFrozen = true };

        OccurrenceRules.FreezePeriodOwner(recorded, recorded with { AssigneeId = Bram }, Zone, late).HasPeriodOwner.Should().BeFalse();
        OccurrenceRules.FreezePeriodOwner(frozen, frozen with { AssigneeId = Bram }, Zone, late).PeriodOwnerId.Should().Be(Cleo);
    }

    // ---- warnings

    [Fact]
    public void UnavailableWarnings_namesThePersonAndTheWeekdayOnlyWhenTheyCannotDoThatDay()
    {
        var user = new User(Bram, "Bram", "#000000", true, Role.Member, [2], new DailyMinutes(1, 1), new DailyMinutes(1, 1), BrowserNotifications.Disabled, Now, Now);

        var tuesday = OccurrenceRules.UnavailableWarnings(user, new DateOnly(2026, 9, 22));
        var wednesday = OccurrenceRules.UnavailableWarnings(user, new DateOnly(2026, 9, 23));

        tuesday.Should().ContainSingle().Which.Should().Match<OccurrenceWarning>(w =>
            w.Code == "assignee_unavailable" && (string)w.Details["userId"]! == Bram && (int)w.Details["weekday"]! == 2);
        wednesday.Should().BeEmpty();
        OccurrenceRules.UnavailableWarnings(null, new DateOnly(2026, 9, 22)).Should().BeEmpty();
    }

    // ---- the transitions and their history

    [Fact]
    public void Complete_claimedWorkTakenOverRecordsEverythingTheHistoryNeeds()
    {
        var current = Open(assignee: null);

        var t = OccurrenceTransitions.Complete(current, Bram, Bram, takeOver: true, 30, Now, Zone);

        t.Action.Should().Be(AuditAction.Complete);
        (t.After.Status, t.After.StatusBeforeCompletion, t.After.CompletedBy, t.After.AssigneeId, t.After.PointsSnapshot).Should().Be((OccurrenceStatus.Done, OccurrenceStatus.Open, Bram, Bram, 30));
        t.Meta!["claimed"].Should().Be(new AuditBool(true));
        t.Meta["takenOver"].Should().Be(new AuditBool(true));
        t.Meta["previousAssigneeId"].Should().Be(AuditNull.Instance);
    }

    [Fact]
    public void Complete_onBehalfLeavesTheAssigneeAndMarksWhetherThePersonWasIt()
    {
        var onBehalf = OccurrenceTransitions.Complete(Open(assignee: Anna), Bram, Anna, takeOver: false, 30, Now, Zone);
        var other = OccurrenceTransitions.Complete(Open(assignee: Anna), Bram, Cleo, takeOver: false, 30, Now, Zone);

        onBehalf.After.AssigneeId.Should().Be(Anna);
        onBehalf.Meta!["wasAssignee"].Should().Be(new AuditBool(true));
        onBehalf.Meta.Properties.Select(p => p.Key).Should().NotContain(["claimed", "takenOver"]);
        other.Meta!["wasAssignee"].Should().Be(new AuditBool(false));
        other.After.AssigneeId.Should().Be(Anna);
    }

    [Fact]
    public void Uncomplete_goesBackToTheStatusBeforeAndClearsTheCompletion()
    {
        var done = Open() with { Status = OccurrenceStatus.Done, StatusBeforeCompletion = OccurrenceStatus.Skipped, CompletedAt = Now, CompletedBy = Anna, PointsSnapshot = 30 };

        var t = OccurrenceTransitions.Uncomplete(done, Now.AddHours(1));

        (t.After.Status, t.After.StatusBeforeCompletion, t.After.CompletedAt, t.After.CompletedBy, t.After.PointsSnapshot).Should().Be((OccurrenceStatus.Skipped, null, null, null, null));
        OccurrenceTransitions.Uncomplete(done with { StatusBeforeCompletion = null }, Now).After.Status.Should().Be(OccurrenceStatus.Open);
    }

    [Fact]
    public void Reschedule_keepsThePlannedDayAndRecordsFromAndToAsDayKeys()
    {
        var current = Open("2026-09-14");

        var t = OccurrenceTransitions.Reschedule(current, new DateOnly(2026, 9, 16), OtherCycleId, Now, Zone);

        t.Action.Should().Be(AuditAction.Reschedule);
        (t.After.PlannedDate, t.After.CycleId).Should().Be((current.PlannedDate, OtherCycleId));
        DayKeys.ToDayKey(t.After.Date, Zone).Should().Be(new DateOnly(2026, 9, 16));
        t.Meta!["from"].Should().Be(new AuditString("2026-09-14"));
        t.Meta["to"].Should().Be(new AuditString("2026-09-16"));
    }

    [Fact]
    public void Claim_isAnAssignOfTheActorMarkedAsAClaim()
    {
        var t = OccurrenceTransitions.Claim(Open(assignee: null), Bram, Now, Zone);

        t.Action.Should().Be(AuditAction.Assign);
        t.After.AssigneeId.Should().Be(Bram);
        t.Meta!["claim"].Should().Be(new AuditBool(true));
    }

    [Fact]
    public void EditCompletion_isAnUpdateWithTheCorrectionReasonAndKeepsTheCycleWhenNoneIsGiven()
    {
        var done = Open() with { Status = OccurrenceStatus.Done, CompletedAt = Now, CompletedBy = Anna };

        var t = OccurrenceTransitions.EditCompletion(done, new EditCompletionCommand(new DateOnly(2026, 9, 17), Now.AddDays(1), Bram), null, Now, Zone);

        t.Action.Should().Be(AuditAction.Update);
        (t.After.CompletedBy, t.After.CycleId).Should().Be((Bram, CycleId));
        t.Meta!["correction"].Should().Be(new AuditString("completion"));
    }

    // ---- the audit entries

    [Fact]
    public void ForChange_aNoOpIsNull()
    {
        var current = Open();

        OccurrenceAudit.ForChange(AuditActor.System, current, current with { UpdatedAt = Now }, AuditAction.Assign, null).Should().BeNull();
    }

    [Fact]
    public void ForChange_listsOnlyTheChangedFieldsAndAddsTheOccurrenceContext()
    {
        var before = Open("2026-09-14");
        var after = before with { AssigneeId = Bram };

        var entry = OccurrenceAudit.ForChange(new AuditActor(Cleo, AuditSource.Ui), before, after, AuditAction.Assign, AuditObject.Of(("claim", true)))!;

        entry.Entity.Should().Be(AuditEntity.Occurrence);
        entry.EntityId.Should().Be(before.Id);
        entry.Before.Properties.Select(p => p.Key).Should().Equal("assigneeId");
        entry.After["assigneeId"].Should().Be(new AuditObjectId(Bram));
        entry.Meta!["claim"].Should().Be(new AuditBool(true));
        ((AuditObject)entry.Meta["occurrence"]!)["date"].Should().Be(new AuditInstant(before.Date));
    }

    [Fact]
    public void ForChange_aClearedSnapshotShowsAsAnExplicitNull()
    {
        var done = Open() with { Status = OccurrenceStatus.Done, StatusBeforeCompletion = OccurrenceStatus.Open, CompletedAt = Now, CompletedBy = Anna, PointsSnapshot = 30 };
        var transition = OccurrenceTransitions.Uncomplete(done, Now);

        var entry = OccurrenceAudit.ForChange(AuditActor.System, done, transition.After, AuditAction.Uncomplete, null)!;

        entry.Before["pointsSnapshot"].Should().Be(new AuditInteger(30));
        entry.After["pointsSnapshot"].Should().Be(AuditNull.Instance);
        entry.After["completedAt"].Should().Be(AuditNull.Instance);
    }

    [Fact]
    public void ForCorrectionDelete_listsEveryFieldInBeforeWithTheCorrectionReason()
    {
        var entry = OccurrenceAudit.ForCorrectionDelete(AuditActor.System, Open());

        entry.Action.Should().Be(AuditAction.Delete);
        entry.After.Count.Should().Be(0);
        entry.Before["taskNameSnapshot"].Should().Be(new AuditString("Badkamer"));
        entry.Meta!["correction"].Should().Be(new AuditString("completion"));
    }

    [Fact]
    public void Fields_aFrozenOwnerThatIsNobodyIsAnExplicitNullAndAMissingOneIsAbsent()
    {
        OccurrenceAudit.Fields(Open() with { PeriodOwnerId = null, PeriodOwnerFrozen = true })["periodOwnerId"].Should().Be(AuditNull.Instance);
        OccurrenceAudit.Fields(Open())["periodOwnerId"].Should().BeNull();
    }

    [Fact]
    public void TaskAudit_lastCompletedAtIsAnUpdateOfThatFieldAloneWithTheOccurrenceInMeta()
    {
        var entry = TaskAudit.ForLastCompletedAt(AuditActor.System, "222222222222222222222222", null, Now, "111111111111111111111111")!;

        entry.Entity.Should().Be(AuditEntity.Task);
        entry.Action.Should().Be(AuditAction.Update);
        entry.Before["lastCompletedAt"].Should().Be(AuditNull.Instance);
        entry.After["lastCompletedAt"].Should().Be(new AuditInstant(Now));
        entry.Meta!["occurrenceId"].Should().Be(new AuditObjectId("111111111111111111111111"));
        TaskAudit.ForLastCompletedAt(AuditActor.System, "222222222222222222222222", Now, Now, "111111111111111111111111").Should().BeNull();
    }

    // ---- the view and the cursor

    [Fact]
    public void View_isOverdueOnlyWhenOpenAndBeforeTodayAndCarriesTheCycleAndWeekIndex()
    {
        var today = new DateOnly(2026, 9, 16);

        var past = OccurrenceView.From(Open("2026-09-14"), Zone, Anchor, today);
        var current = OccurrenceView.From(Open("2026-09-16"), Zone, Anchor, today);
        var doneInThePast = OccurrenceView.From(Open("2026-09-14") with { Status = OccurrenceStatus.Done }, Zone, Anchor, today);
        var later = OccurrenceView.From(Open("2026-10-12"), Zone, Anchor, today);

        (past.IsOverdue, current.IsOverdue, doneInThePast.IsOverdue).Should().Be((true, false, false));
        (later.CycleIndex, later.WeekIndex).Should().Be((1, 0));
        (past.CycleIndex, past.WeekIndex).Should().Be((0, 0));
    }

    [Fact]
    public void View_movedFromIsThePlannedDayOnlyWhenTheOccurrenceSitsOnAnotherDay()
    {
        var moved = Open("2026-09-14") with { Date = DayKeys.FromDayKey(new DateOnly(2026, 9, 16), Zone) };

        OccurrenceView.From(moved, Zone, Anchor, Anchor).MovedFrom.Should().Be(new DateOnly(2026, 9, 14));
        OccurrenceView.From(Open(), Zone, Anchor, Anchor).MovedFrom.Should().BeNull();
    }

    [Fact]
    public void Cursor_roundTripsAndRejectsWhatItDidNotProduce()
    {
        var cursor = new OccurrenceCursor(Now, "Wastafel", Anna);

        OccurrenceCursor.TryDecode(cursor.Encode(), out var decoded).Should().BeTrue();

        decoded.Should().Be(cursor);
        OccurrenceCursor.TryDecode("garbage", out _).Should().BeFalse();
        OccurrenceCursor.TryDecode(null, out _).Should().BeFalse();
        OccurrenceCursor.TryDecode(new OccurrenceCursor(Now, "x", "not-an-id").Encode(), out _).Should().BeFalse();
    }
}
