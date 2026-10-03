using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Users;

namespace Huishoudplanner.Application.Tests.Occurrences;

/// <summary>
/// Skipping, rescheduling, assigning and claiming: <c>occurrences.test.ts</c> (skip, claim), <c>reschedule.test.ts</c> (reschedule and assign).
/// </summary>
public sealed class OccurrenceMovesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuditObjectId Id(string hex) => new(hex);

    private static DateOnly Day(string day) => DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Persoon 2 cannot do Tuesdays, as in <c>reschedule.test.ts</c>.</summary>
    private static OccurrenceWorld WorldWhereP2CannotDoTuesdays()
    {
        var w = new OccurrenceWorld();
        var index = w.People.Users.FindIndex(u => u.Id == w.P2.Id);
        w.People.Users[index] = w.People.Users[index] with { UnavailableWeekdays = [2] };
        return w;
    }

    // ---- skip

    [Fact]
    public async Task Skip_marksTheOccurrenceSkippedWithATrimmedReasonAndAuditsIt()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-09-24");

        var view = (await w.Service.SkipAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, "  geen tijd ", Ct)).AsT0;

        (view.Occurrence.Status, view.Occurrence.SkipReason).Should().Be((OccurrenceStatus.Skipped, "geen tijd"));
        w.Entries(AuditEntity.Occurrence, AuditAction.Skip).Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Skip_noReasonIsStoredAsNull(string? reason)
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-09-24");

        var view = (await w.Service.SkipAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, reason, Ct)).AsT0;

        view.Occurrence.SkipReason.Should().BeNull();
    }

    [Fact]
    public async Task Skip_aReasonOver500CharactersIsAValidationError()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-09-24");

        var result = await w.Service.SkipAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new string('x', 501), Ct);

        result.AsT2.Errors.Should().ContainKey("reason");
        w.Writes.Should().Be(0);
    }

    [Theory]
    [InlineData(OccurrenceStatus.Done)]
    [InlineData(OccurrenceStatus.Skipped)]
    public async Task Skip_onlyAnOpenOccurrence(OccurrenceStatus status)
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-09-24", status: status);

        var result = await w.Service.SkipAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, null, Ct);

        result.AsT3.Code.Should().Be("invalid_transition");
        result.AsT3.Extensions!["status"].Should().Be(status == OccurrenceStatus.Done ? "done" : "skipped");
    }

    // ---- reschedule

    [Fact]
    public async Task Reschedule_movesTheOccurrenceKeepsThePlannedDayAndAuditsFromAndTo()
    {
        var w = WorldWhereP2CannotDoTuesdays();
        var occurrence = w.Seed(w.Weekly, "2026-09-14", w.P2);

        var change = (await w.Service.RescheduleAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Day("2026-09-16"), Ct)).AsT0;

        change.View.Date.Should().Be(Day("2026-09-16"));
        change.View.PlannedDate.Should().Be(Day("2026-09-14"));
        change.View.MovedFrom.Should().Be(Day("2026-09-14"));
        change.Warnings.Should().BeEmpty();
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Reschedule).Single();
        entry.Meta!["from"].Should().Be(new AuditString("2026-09-14"));
        entry.Meta["to"].Should().Be(new AuditString("2026-09-16"));
        var context = (AuditObject)entry.Meta["occurrence"]!;
        context["taskNameSnapshot"].Should().Be(new AuditString("Badkamer schoonmaken"));
        context["roomNameSnapshot"].Should().Be(new AuditString("Badkamer"));
        entry.Before["date"].Should().Be(new AuditInstant(new DateTimeOffset(2026, 9, 13, 22, 0, 0, TimeSpan.Zero)));
        entry.After["date"].Should().Be(new AuditInstant(new DateTimeOffset(2026, 9, 15, 22, 0, 0, TimeSpan.Zero)));
        entry.Actor.Source.Should().Be(AuditSource.Ui);
    }

    [Fact]
    public async Task Reschedule_toADayTheAssigneeCannotDoIsAllowedWithAWarning()
    {
        var w = WorldWhereP2CannotDoTuesdays();
        var occurrence = w.Seed(w.Weekly, "2026-09-21", w.P2);

        var change = (await w.Service.RescheduleAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Day("2026-09-22"), Ct)).AsT0;

        change.View.Date.Should().Be(Day("2026-09-22"));
        var warning = change.Warnings.Should().ContainSingle().Subject;
        warning.Code.Should().Be("assignee_unavailable");
        warning.Details.Should().Equal(new Dictionary<string, object?> { ["userId"] = w.P2.Id, ["weekday"] = 2 });
    }

    [Fact]
    public async Task Reschedule_toTheOtherCycleMovesTheOccurrenceToThatCycle()
    {
        var w = WorldWhereP2CannotDoTuesdays();
        var occurrence = w.Seed(w.Weekly, "2026-10-11", w.P2);
        occurrence.CycleId.Should().Be(w.Cycle0.Id);

        var change = (await w.Service.RescheduleAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Day("2026-10-12"), Ct)).AsT0;

        change.View.Occurrence.CycleId.Should().Be(w.Cycle1.Id);
        (change.View.Date, change.View.PlannedDate).Should().Be((Day("2026-10-12"), Day("2026-10-11")));
        change.View.CycleIndex.Should().Be(1);
    }

    [Fact]
    public async Task Reschedule_backToThePlannedDayClearsMovedFrom()
    {
        var w = WorldWhereP2CannotDoTuesdays();
        var occurrence = w.Seed(w.Weekly, "2026-09-14", w.P2);
        (await w.Service.RescheduleAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Day("2026-09-16"), Ct)).IsT0.Should().BeTrue();

        var change = (await w.Service.RescheduleAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Day("2026-09-14"), Ct)).AsT0;

        (change.View.Date, change.View.MovedFrom).Should().Be((Day("2026-09-14"), (DateOnly?)null));
    }

    [Fact]
    public async Task Reschedule_toTheSameDayWritesAndAuditsNothing()
    {
        var w = WorldWhereP2CannotDoTuesdays();
        var occurrence = w.Seed(w.Weekly, "2026-09-14", w.P2);

        var change = (await w.Service.RescheduleAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Day("2026-09-14"), Ct)).AsT0;

        change.Warnings.Should().BeEmpty();
        w.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Reschedule_aDayThatIsNotGeneratedIsRefusedWithTheDate()
    {
        var w = WorldWhereP2CannotDoTuesdays();
        var occurrence = w.Seed(w.Weekly, "2026-09-14", w.P2);

        var result = await w.Service.RescheduleAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Day("2026-12-01"), Ct);

        result.AsT3.Code.Should().Be("cycle_not_generated");
        result.AsT3.Extensions.Should().Equal(new Dictionary<string, object?> { ["date"] = "2026-12-01" });
        w.Writes.Should().Be(0);
    }

    [Theory]
    [InlineData(OccurrenceStatus.Done)]
    [InlineData(OccurrenceStatus.Skipped)]
    public async Task Reschedule_onlyAnOpenOccurrence(OccurrenceStatus status)
    {
        var w = WorldWhereP2CannotDoTuesdays();
        var occurrence = w.Seed(w.Weekly, "2026-09-14", w.P2, status);

        var result = await w.Service.RescheduleAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Day("2026-09-15"), Ct);

        result.AsT3.Code.Should().Be("invalid_transition");
    }

    // ---- assign

    [Fact]
    public async Task Assign_reassignsWithAnAuditEntryAndWarnsWhenThePersonIsUnavailableThatDay()
    {
        var w = WorldWhereP2CannotDoTuesdays();
        var occurrence = w.Seed(w.Weekly, "2026-09-28", w.P2);

        var first = (await w.Service.AssignAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, w.P1.Id, Ct)).AsT0;

        first.View.Occurrence.AssigneeId.Should().Be(w.P1.Id);
        first.Warnings.Should().BeEmpty();
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Assign).Single();
        entry.Before.Properties.Select(p => p.Key).Should().Equal("assigneeId");
        entry.Before["assigneeId"].Should().Be(Id(w.P2.Id));
        entry.After["assigneeId"].Should().Be(Id(w.P1.Id));

        (await w.Service.RescheduleAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Day("2026-09-29"), Ct)).IsT0.Should().BeTrue();
        var back = (await w.Service.AssignAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, w.P2.Id, Ct)).AsT0;
        back.Warnings.Should().ContainSingle().Which.Code.Should().Be("assignee_unavailable");
    }

    [Fact]
    public async Task Assign_anyoneIsNullAndAnUnknownOrInactivePersonIsRefused()
    {
        var w = WorldWhereP2CannotDoTuesdays();
        var occurrence = w.Seed(w.Weekly, "2026-10-19", w.P2);
        var gone = w.People.Add("Weg", active: false);

        var anyone = (await w.Service.AssignAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, null, Ct)).AsT0;
        var unknown = await w.Service.AssignAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, "0123456789abcdef01234567", Ct);
        var inactive = await w.Service.AssignAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, gone.Id, Ct);
        var malformed = await w.Service.AssignAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, "x", Ct);

        anyone.View.Occurrence.AssigneeId.Should().BeNull();
        unknown.AsT2.Errors["assigneeId"].Should().Equal("unknown_user");
        inactive.AsT2.Errors["assigneeId"].Should().Equal("inactive_user");
        malformed.AsT2.Errors["assigneeId"].Should().Equal("invalid_object_id");
    }

    [Fact]
    public async Task Assign_thePersonWhoAlreadyHasItWritesNothingAndWarnsNothing()
    {
        var w = WorldWhereP2CannotDoTuesdays();
        var occurrence = w.Seed(w.Weekly, "2026-09-22", w.P2);

        var change = (await w.Service.AssignAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, w.P2.Id, Ct)).AsT0;

        change.Warnings.Should().BeEmpty();
        w.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Assign_onlyAnOpenOccurrence()
    {
        var w = WorldWhereP2CannotDoTuesdays();
        var occurrence = w.Seed(w.Weekly, "2026-09-22", w.P2, OccurrenceStatus.Done);

        var result = await w.Service.AssignAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, w.P1.Id, Ct);

        result.AsT3.Extensions.Should().Equal(new Dictionary<string, object?> { ["status"] = "done", ["action"] = "assign" });
    }

    [Fact]
    public async Task Assign_freezesThePeriodOwnerOnlyWhenItChangesHandsAfterTheWeekEnded()
    {
        var w = WorldWhereP2CannotDoTuesdays();
        w.Clock.Now = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var late = w.Seed(w.Weekly, "2026-09-14", w.P2);
        var same = w.Seed(w.Twice, "2026-09-15", w.P2);

        var changed = (await w.Service.AssignAsync(OccurrenceWorld.Actor(w.P1), late.Id, w.P1.Id, Ct)).AsT0;
        var unchanged = (await w.Service.AssignAsync(OccurrenceWorld.Actor(w.P1), same.Id, w.P2.Id, Ct)).AsT0;

        (changed.View.Occurrence.PeriodOwnerFrozen, changed.View.Occurrence.PeriodOwnerId).Should().Be((true, w.P2.Id));
        unchanged.View.Occurrence.PeriodOwnerFrozen.Should().BeFalse();
    }

    // ---- claim

    [Fact]
    public async Task Claim_takesAnUnassignedOccurrenceAndAuditsItAsAnAssignWithTheClaimMarker()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-09-30");

        var view = (await w.Service.ClaimAsync(OccurrenceWorld.Actor(w.P2), occurrence.Id, Ct)).AsT0;

        view.Occurrence.AssigneeId.Should().Be(w.P2.Id);
        var entry = w.Entries(AuditEntity.Occurrence, AuditAction.Assign).Single();
        entry.Meta!["claim"].Should().Be(new AuditBool(true));
        entry.Actor.ActorId.Should().Be(w.P2.Id);
    }

    [Fact]
    public async Task Claim_aSecondClaimIsAlreadyClaimed()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-09-30");
        (await w.Service.ClaimAsync(OccurrenceWorld.Actor(w.P2), occurrence.Id, Ct)).IsT0.Should().BeTrue();

        var again = await w.Service.ClaimAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, Ct);

        again.AsT3.Code.Should().Be("already_claimed");
    }

    [Fact]
    public async Task Claim_anOccurrenceThatAlreadyHasAnAssigneeIsAlreadyClaimed()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-10-01", w.P1);

        var result = await w.Service.ClaimAsync(OccurrenceWorld.Actor(w.P2), occurrence.Id, Ct);

        result.AsT3.Code.Should().Be("already_claimed");
    }

    [Theory]
    [InlineData(OccurrenceStatus.Skipped)]
    [InlineData(OccurrenceStatus.Done)]
    public async Task Claim_aDoneOrSkippedOccurrenceIsInvalidTransitionAndNothingIsWritten(OccurrenceStatus status)
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-10-14", status: status);

        var result = await w.Service.ClaimAsync(OccurrenceWorld.Actor(w.P2), occurrence.Id, Ct);

        result.AsT3.Code.Should().Be("invalid_transition");
        result.AsT3.Extensions!["action"].Should().Be("claim");
        w.Writes.Should().Be(0);
        w.Stored(occurrence).AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task Claim_anUnknownOccurrenceIsNotFound()
    {
        var w = new OccurrenceWorld();

        var result = await w.Service.ClaimAsync(OccurrenceWorld.Actor(w.P1), "0123456789abcdef01234567", Ct);

        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Claim_whenAnotherClaimWinsBetweenReadAndWriteTheLoserIsAlreadyClaimed()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-10-07");
        w.Occurrences.ConcurrentWriteBeforeNextUpdate = () =>
            w.Occurrences.Items[0] = w.Occurrences.Items[0] with { AssigneeId = w.P1.Id };

        var result = await w.Service.ClaimAsync(OccurrenceWorld.Actor(w.P2), occurrence.Id, Ct);

        result.AsT3.Code.Should().Be("already_claimed");
        w.Entries(AuditEntity.Occurrence).Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_whenTheOccurrenceIsCompletedBetweenReadAndWriteTheLoserIsInvalidTransition()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Twice, "2026-10-07");
        w.Occurrences.ConcurrentWriteBeforeNextUpdate = () =>
            w.Occurrences.Items[0] = w.Occurrences.Items[0] with { Status = OccurrenceStatus.Done };

        var result = await w.Service.ClaimAsync(OccurrenceWorld.Actor(w.P2), occurrence.Id, Ct);

        result.AsT3.Extensions.Should().Equal(new Dictionary<string, object?> { ["status"] = "done", ["action"] = "claim" });
    }

    [Fact]
    public async Task Claim_freezesThePeriodOwnerAsUnassignedWhenTheWeekHasEnded()
    {
        var w = new OccurrenceWorld();
        w.Clock.Now = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var occurrence = w.Seed(w.Twice, "2026-09-14");

        var view = (await w.Service.ClaimAsync(OccurrenceWorld.Actor(w.P2), occurrence.Id, Ct)).AsT0;

        (view.Occurrence.PeriodOwnerFrozen, view.Occurrence.PeriodOwnerId).Should().Be((true, null));
    }
}
