using Huishoudplanner.Application.Tests.Occurrences;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Users;
using static Huishoudplanner.Application.Tests.Points.RedemptionWorld;

namespace Huishoudplanner.Application.Tests.Points;

/// <summary>
/// <c>points-redemptions.test.ts</c> as use case tests: booking a redemption (the balance check, the idempotency key, the permissions, the audit entry)
/// and undoing it (the same-day lock). The HTTP shape, the real transaction and the race of two bookings at once are in the integration tests.
/// </summary>
public sealed class RedemptionServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<OneOf.OneOf<BookedRedemption, ValidationErrors, Forbidden, ConflictError, SettingsMissing, PortError>> Book(
        RedemptionWorld w, User by, int points, string? note = null, string? requestId = null, User? @for = null) =>
        w.Service.BookAsync(ActorOf(by), new RedemptionCommand(@for?.Id, points, note, requestId), Ct);

    private static BookedRedemption Booked(OneOf.OneOf<BookedRedemption, ValidationErrors, Forbidden, ConflictError, SettingsMissing, PortError> result) => result.AsT0;

    private static RedemptionWorld Earned(int points, bool otherToo = false)
    {
        var w = new RedemptionWorld();
        w.Store.SeedEarned(w.P1.Id, points);
        if (otherToo)
        {
            w.Store.SeedEarned(w.P2.Id, points);
        }

        return w;
    }

    private static int Writes(RedemptionWorld w) => w.Store.Inserts + w.Store.Deletes + w.Store.Guards.Values.Sum() + w.Occ.Audit.Entries.Count;

    // ---- booking

    [Fact]
    public async Task Book_createsANegativeLiveEntryDatedTodayWithTheFactorAndCurrencyOfTheMoment()
    {
        var w = Earned(10);
        w.Occ.SettingsStore.Document = w.Occ.SettingsStore.Document! with { CentsPerPoint = 25, CurrencyCode = "EUR" };

        var booked = Booked(await Book(w, w.P1, 4, "Pizza", KeyA));

        booked.Created.Should().BeTrue();
        var entry = booked.View.Entry;
        entry.Should().BeEquivalentTo(new
        {
            Kind = PointEntryKind.Redemption,
            PersonId = w.P1.Id,
            Amount = -4,
            Date = At("2026-09-16"),
            WeekStart = At("2026-09-14"),
            PeriodStart = (DateTimeOffset?)null,
            OccurrenceId = (string?)null,
            TaskId = (string?)null,
            TitleSnapshot = string.Empty,
            Note = "Pizza",
            CentsPerPointSnapshot = 25,
            CurrencyCodeSnapshot = "EUR",
            Source = PointEntrySource.Live,
            CreatedAt = OccurrenceWorld.Now,
            UpdatedAt = OccurrenceWorld.Now,
        });
        (booked.View.Date, booked.View.WeekStart).Should().Be((new DateOnly(2026, 9, 16), new DateOnly(2026, 9, 14)));
        w.Redemptions.Should().ContainSingle().Which.Id.Should().Be(entry.Id);
    }

    [Fact]
    public async Task Book_auditsOncePerBookingAsAPointsCreateNamingTheActorWithoutTheRequestKey()
    {
        var w = Earned(10);

        await Book(w, w.P1, 4, "Pizza", KeyA);

        var entry = w.PointsAudit().Should().ContainSingle().Subject;
        (entry.Action, entry.Actor.ActorId).Should().Be((AuditAction.Create, w.P1.Id));
        entry.Meta.Should().Be(AuditObject.Of(("reason", "redemption")));
        entry.After["amount"].Should().Be(new AuditInteger(-4));
        entry.After["note"].Should().Be(new AuditString("Pizza"));
        entry.After.Keys.Should().NotContain("requestId");
        entry.EntityId.Should().Be(w.Redemptions[0].Id);
    }

    [Fact]
    public async Task Book_trimsTheNoteAndTreatsAnEmptyNoteAsNone()
    {
        var w = Earned(10);

        var none = Booked(await Book(w, w.P1, 1)).View.Entry.Note;
        var blank = Booked(await Book(w, w.P1, 1, "   ")).View.Entry.Note;
        var trimmed = Booked(await Book(w, w.P1, 1, "  Ijsje  ")).View.Entry.Note;

        (none, blank, trimmed).Should().Be((null, null, "Ijsje"));
    }

    [Fact]
    public async Task Book_aFactorOf0IsKeptAsTheSnapshotAndAnUnsetCurrencyIsTheDefault()
    {
        var w = Earned(5);

        var entry = Booked(await Book(w, w.P1, 1)).View.Entry;

        (entry.CentsPerPointSnapshot, entry.CurrencyCodeSnapshot).Should().Be((0, "EUR"));
    }

    [Fact]
    public async Task Book_keepsTheFactorOfEachBookingWhenTheSettingsChangeInBetween()
    {
        var w = Earned(10);
        w.Occ.SettingsStore.Document = w.Occ.SettingsStore.Document! with { CentsPerPoint = 20, CurrencyCode = "EUR" };
        var first = Booked(await Book(w, w.P1, 2)).View.Entry;
        w.Occ.SettingsStore.Document = w.Occ.SettingsStore.Document! with { CentsPerPoint = 30, CurrencyCode = "USD" };
        var second = Booked(await Book(w, w.P1, 3)).View.Entry;

        (first.CentsPerPointSnapshot, first.CurrencyCodeSnapshot).Should().Be((20, "EUR"));
        (second.CentsPerPointSnapshot, second.CurrencyCodeSnapshot).Should().Be((30, "USD"));
    }

    // ---- who may book

    [Fact]
    public async Task Book_aMemberForSomebodyElseIsForbiddenAndWritesNothing()
    {
        var w = Earned(6, otherToo: true);
        var before = Writes(w);

        var result = await Book(w, w.P2, 1, @for: w.P1);

        result.AsT2.Detail.Should().NotBeEmpty();
        result.AsT2.Code.Should().BeNull();
        Writes(w).Should().Be(before);
        w.Transactions.Runs.Should().Be(0, "the permission is checked before anything is read");
    }

    [Fact]
    public async Task Book_aMemberMayNameThemselvesAndAnAdministratorMayBookForAnyone()
    {
        var w = Earned(6, otherToo: true);

        Booked(await Book(w, w.P2, 2)).View.Entry.PersonId.Should().Be(w.P2.Id);
        Booked(await Book(w, w.P2, 1, @for: w.P2)).View.Entry.PersonId.Should().Be(w.P2.Id);
        var forOther = Booked(await Book(w, w.Admin, 2, @for: w.P2));

        forOther.View.Entry.PersonId.Should().Be(w.P2.Id);
        w.PointsAudit().Last().Actor.ActorId.Should().Be(w.Admin.Id, "the audit entry names the administrator who booked it");
    }

    [Fact]
    public async Task Book_aPersonThatIsUnknownOrInactiveIsRefusedOnPersonIdAndWritesNothing()
    {
        var w = Earned(5);
        var lodger = w.Occ.People.Add("Logé", active: false);

        var unknown = await w.Service.BookAsync(ActorOf(w.Admin), new RedemptionCommand("0000000000000000000000ff", 1, null, null), Ct);
        var inactive = await Book(w, w.Admin, 1, @for: lodger);

        unknown.AsT1.Errors["personId"].Should().Equal("unknown_user");
        inactive.AsT1.Errors["personId"].Should().Equal("inactive_user");
        w.Redemptions.Should().BeEmpty();
        w.Occ.Audit.Entries.Should().BeEmpty();
    }

    // ---- the balance

    [Fact]
    public async Task Book_aBookingAboveTheBalanceIsInsufficientBalanceWithTheBalanceAndTheRequestedPointsAndWritesNothing()
    {
        var w = Earned(5);

        var result = await Book(w, w.P1, 6);

        var conflict = result.AsT3;
        conflict.Code.Should().Be("insufficient_balance");
        conflict.Extensions.Should().BeEquivalentTo(new Dictionary<string, object?> { ["balance"] = 5L, ["requested"] = 6 });
        w.Redemptions.Should().BeEmpty();
        w.Occ.Audit.Entries.Should().BeEmpty();
        w.Transactions.Aborts.Should().Be(1, "everything the attempt wrote, the guard included, is rolled back");
    }

    [Fact]
    public async Task Book_theExactBalanceIsAllowedAndTheNextPointIsNot()
    {
        var w = Earned(5);

        Booked(await Book(w, w.P1, 5)).Created.Should().BeTrue();
        var again = await Book(w, w.P1, 1);

        again.AsT3.Extensions.Should().BeEquivalentTo(new Dictionary<string, object?> { ["balance"] = 0L, ["requested"] = 1 });
    }

    [Fact]
    public async Task Book_theBalanceIsTheWholeLedgerBonusesAndEarlierRedemptionsIncluded()
    {
        var w = new RedemptionWorld();
        w.Store.SeedEarned(w.P1.Id, 3);
        w.Store.SeedEarned(w.P1.Id, 2, PointEntryKind.BonusWeekDone);
        w.Store.Seed(w.P1.Id, 4, date: At("2026-09-01"));

        var tooMuch = await Book(w, w.P1, 2);

        tooMuch.AsT3.Extensions!["balance"].Should().Be(1L);
        Booked(await Book(w, w.P1, 1)).Created.Should().BeTrue();
    }

    [Fact]
    public async Task Book_theBalanceOfAnotherPersonDoesNotCount()
    {
        var w = Earned(10, otherToo: true);
        w.Store.Seed(w.P2.Id, 10);

        Booked(await Book(w, w.P1, 10)).Created.Should().BeTrue();
    }

    [Fact]
    public async Task Book_writesTheGuardOfThePersonBeforeItReadsTheBalance()
    {
        var w = Earned(10);

        await Book(w, w.P1, 4);

        w.Store.Calls.Should().Equal("guard", "balance", "insert");
        w.Store.Guards.Should().Equal(new Dictionary<string, int> { [w.P1.Id] = 1 });
    }

    [Fact]
    public async Task Book_theGuardIsPerPerson()
    {
        var w = Earned(10, otherToo: true);

        await Book(w, w.P1, 4);
        await Book(w, w.P2, 4);

        w.Store.Guards.Should().Equal(new Dictionary<string, int> { [w.P1.Id] = 1, [w.P2.Id] = 1 });
    }

    // ---- the request key

    [Fact]
    public async Task Book_aRepeatedRequestKeyReplaysTheStoredBookingAndWritesAndAuditsNothing()
    {
        var w = Earned(10);
        var first = Booked(await Book(w, w.P1, 3, "Film", KeyA));
        var before = Writes(w);

        var replay = Booked(await Book(w, w.P1, 3, "Film", KeyA));

        replay.Created.Should().BeFalse();
        replay.View.Entry.Should().Be(first.View.Entry);
        Writes(w).Should().Be(before);
        w.Redemptions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Book_aReplayStillWorksOnAnotherDayAndIsNotStuckOnTheBalanceItWouldExceedNow()
    {
        var w = Earned(10);
        Booked(await Book(w, w.P1, 7, requestId: KeyB));
        w.SetNow(new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero));

        var replay = Booked(await Book(w, w.P1, 7, requestId: KeyB));

        replay.Created.Should().BeFalse();
    }

    [Fact]
    public async Task Book_theSameKeyForAnotherRequestIsAnIdempotencyKeyConflict()
    {
        var w = Earned(10, otherToo: true);
        Booked(await Book(w, w.P1, 3, "Film", KeyA));
        var before = Writes(w);

        var differentPoints = await Book(w, w.P1, 4, "Film", KeyA);
        var differentNote = await Book(w, w.P1, 3, "Andere", KeyA);
        var noNote = await Book(w, w.P1, 3, null, KeyA);
        var otherPerson = await Book(w, w.P2, 3, "Film", KeyA);

        foreach (var conflict in new[] { differentPoints, differentNote, noNote, otherPerson })
        {
            conflict.AsT3.Code.Should().Be("idempotency_key_conflict");
        }

        Writes(w).Should().Be(before);
        w.Redemptions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Book_theReplayComparesTheTrimmedNote()
    {
        var w = Earned(10);
        Booked(await Book(w, w.P1, 3, "Film", KeyA));

        Booked(await Book(w, w.P1, 3, "  Film  ", KeyA)).Created.Should().BeFalse();
    }

    [Fact]
    public async Task Book_aReplayDoesNotWriteTheGuard()
    {
        var w = Earned(10);
        Booked(await Book(w, w.P1, 3, requestId: KeyA));
        var guards = w.Store.Guards.Values.Sum();

        await Book(w, w.P1, 3, requestId: KeyA);

        w.Store.Guards.Values.Sum().Should().Be(guards);
    }

    [Fact]
    public async Task Book_losingTheRaceForTheKeyReplaysTheWinnerWhenItIsTheSameRequest()
    {
        var w = Earned(10);
        w.Store.RaceOnNextInsert(KeyA, w.P1.Id, 3, "Film");

        var booked = Booked(await Book(w, w.P1, 3, "Film", KeyA));

        booked.Created.Should().BeFalse();
        w.Redemptions.Should().HaveCount(1);
        w.Occ.Audit.Entries.Should().BeEmpty("the loser's attempt was rolled back and the winner's audit entry belongs to the winner");
        w.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Book_losingTheRaceForTheKeyIsAConflictWhenTheWinnerIsAnotherRequest()
    {
        var w = Earned(10);
        w.Store.RaceOnNextInsert(KeyA, w.P1.Id, 5, "Iets anders");

        var result = await Book(w, w.P1, 3, "Film", KeyA);

        result.AsT3.Code.Should().Be("idempotency_key_conflict");
        w.Redemptions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Book_aKeyHeldByARecordNobodyCanSeeIsAConflictAfterTheAttemptsRunOut()
    {
        var w = Earned(10);
        w.Store.AlwaysTakenKey = KeyA;

        var result = await Book(w, w.P1, 3, requestId: KeyA);

        result.AsT3.Code.Should().Be("idempotency_key_conflict");
        w.Transactions.Runs.Should().Be(3);
        w.Redemptions.Should().BeEmpty();
    }

    // ---- validation and the failures

    [Fact]
    public async Task Book_aMalformedRequestIsRefusedBeforeAnythingRuns()
    {
        var w = Earned(5);

        var result = await w.Service.BookAsync(ActorOf(w.P1), new RedemptionCommand(null, 0, new string('x', 201), "kort"), Ct);

        result.AsT1.Errors.Keys.Should().BeEquivalentTo("points", "note", "requestId");
        w.Transactions.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Book_missingSettingsAreSettingsMissingAndWriteNothing()
    {
        var w = Earned(5);
        w.Occ.SettingsStore.Document = null;

        var result = await Book(w, w.P1, 1);

        result.IsT4.Should().BeTrue();
        w.Redemptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Book_aTransactionThatKeepsConflictingIsAConflictError()
    {
        var w = Earned(5);
        w.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "A concurrent change won the write; retry the request.");

        var result = await Book(w, w.P1, 1);

        result.AsT3.Code.Should().Be("write_conflict");
    }

    [Fact]
    public async Task Book_aFailingAuditWriteRollsTheBookingBackWithIt()
    {
        var w = Earned(5);
        w.Occ.Audit.Failure = new PortError("audit down");

        var result = await Book(w, w.P1, 1);

        result.AsT5.Message.Should().Be("audit down");
        w.Redemptions.Should().BeEmpty();
        w.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Book_aFailingStoreIsAPortError()
    {
        var w = Earned(5);
        w.Store.Failure = new PortError("redemptions.failed");

        (await Book(w, w.P1, 1)).IsT5.Should().BeTrue();
    }

    // ---- undo

    private static async Task<PointEntry> BookedBy(RedemptionWorld w, User person, int points = 3) =>
        Booked(await Book(w, person, points, "Taart")).View.Entry;

    [Fact]
    public async Task Undo_theOwnerUndoesOnTheSameDayTheEntryGoesAndOneDeleteIsAuditedThatKeepsTheFields()
    {
        var w = Earned(10, otherToo: true);
        var entry = await BookedBy(w, w.P2);
        w.Occ.Audit.Entries.Clear();

        var result = await w.Service.UndoAsync(ActorOf(w.P2), entry.Id, Ct);

        result.IsT0.Should().BeTrue();
        w.Redemptions.Should().BeEmpty();
        var audited = w.PointsAudit().Should().ContainSingle().Subject;
        (audited.Action, audited.EntityId, audited.Actor.ActorId).Should().Be((AuditAction.Delete, entry.Id, w.P2.Id));
        audited.After.Count.Should().Be(0);
        audited.Before["amount"].Should().Be(new AuditInteger(-3));
        audited.Before["note"].Should().Be(new AuditString("Taart"));
        audited.Before.Keys.Should().NotContain("requestId");
        audited.Meta.Should().Be(AuditObject.Of(("reason", "redemption_undone")));
    }

    [Fact]
    public async Task Undo_theOwnerIsLockedOnALaterDayWhileAnAdministratorMayUndoAtAnyTime()
    {
        var w = Earned(10, otherToo: true);
        var entry = await BookedBy(w, w.P2);
        w.SetNow(new DateTimeOffset(2026, 9, 17, 0, 30, 0, TimeSpan.Zero)); // 02:30 on Thursday in Amsterdam
        var before = Writes(w);

        var locked = await w.Service.UndoAsync(ActorOf(w.P2), entry.Id, Ct);

        locked.AsT3.Code.Should().Be("redemption_locked");
        Writes(w).Should().Be(before);
        w.Redemptions.Should().HaveCount(1);
        w.SetNow(new DateTimeOffset(2027, 1, 5, 9, 0, 0, TimeSpan.Zero));
        (await w.Service.UndoAsync(ActorOf(w.Admin), entry.Id, Ct)).IsT0.Should().BeTrue();
        w.Redemptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Undo_theOwnerKeepsTheSameDayUntilLocalMidnightInTheHouseholdTimezone()
    {
        var w = Earned(10, otherToo: true);
        var entry = await BookedBy(w, w.P2);
        w.SetNow(new DateTimeOffset(2026, 9, 16, 21, 59, 0, TimeSpan.Zero)); // 23:59 on Wednesday in Amsterdam

        (await w.Service.UndoAsync(ActorOf(w.P2), entry.Id, Ct)).IsT0.Should().BeTrue();
    }

    [Fact]
    public async Task Undo_anotherMemberIsForbiddenWithPermissionDenied()
    {
        var w = Earned(10);
        var entry = await BookedBy(w, w.P1);
        var before = Writes(w);

        var result = await w.Service.UndoAsync(ActorOf(w.P2), entry.Id, Ct);

        result.AsT3.Code.Should().BeNull();
        Writes(w).Should().Be(before);
        w.Redemptions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Undo_thePersonItWasBookedForMayUndoEvenWhenAnAdministratorBookedIt()
    {
        var w = Earned(10, otherToo: true);
        var entry = Booked(await Book(w, w.Admin, 2, @for: w.P2)).View.Entry;

        (await w.Service.UndoAsync(ActorOf(w.P2), entry.Id, Ct)).IsT0.Should().BeTrue();
    }

    [Fact]
    public async Task Undo_anUnknownIdAndAnEntryThatIsNotARedemptionAreNotFound()
    {
        var w = Earned(10);
        var execution = w.Ledger.Items.Single();

        (await w.Service.UndoAsync(ActorOf(w.Admin), "0000000000000000000000ff", Ct)).IsT1.Should().BeTrue();
        (await w.Service.UndoAsync(ActorOf(w.Admin), execution.Id, Ct)).IsT1.Should().BeTrue();
        w.Ledger.Items.Should().ContainSingle("a derived entry is never undone");
    }

    [Fact]
    public async Task Undo_anIdThatIsNoIdIsAValidationError()
    {
        var w = Earned(10);

        (await w.Service.UndoAsync(ActorOf(w.Admin), "nope", Ct)).AsT2.Errors["id"].Should().Equal("invalid_object_id");
    }

    [Fact]
    public async Task Undo_aSecondUndoIsNotFoundAndWritesNothing()
    {
        var w = Earned(10);
        var entry = await BookedBy(w, w.P1);
        await w.Service.UndoAsync(ActorOf(w.P1), entry.Id, Ct);
        var before = Writes(w);

        var again = await w.Service.UndoAsync(ActorOf(w.P1), entry.Id, Ct);

        again.IsT1.Should().BeTrue();
        Writes(w).Should().Be(before);
    }

    [Fact]
    public async Task Undo_aFailingAuditWriteKeepsTheRedemption()
    {
        var w = Earned(10);
        var entry = await BookedBy(w, w.P1);
        w.Occ.Audit.Entries.Clear();
        w.Occ.Audit.Failure = new PortError("audit down");

        var result = await w.Service.UndoAsync(ActorOf(w.P1), entry.Id, Ct);

        result.AsT6.Message.Should().Be("audit down");
        w.Redemptions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Undo_missingSettingsAreSettingsMissing()
    {
        var w = Earned(10);
        var entry = await BookedBy(w, w.P1);
        w.Occ.SettingsStore.Document = null;

        (await w.Service.UndoAsync(ActorOf(w.P1), entry.Id, Ct)).IsT5.Should().BeTrue();
    }

    // ---- the balance after an undo

    [Fact]
    public async Task Undo_givesThePointsBackSoTheyCanBeBookedAgain()
    {
        var w = Earned(5);
        var entry = await BookedBy(w, w.P1, 5);
        (await Book(w, w.P1, 1)).IsT3.Should().BeTrue();

        await w.Service.UndoAsync(ActorOf(w.P1), entry.Id, Ct);

        Booked(await Book(w, w.P1, 5)).Created.Should().BeTrue();
    }

    // ---- count

    [Fact]
    public async Task Count_countsTheRedemptionsAndNothingElse()
    {
        var w = Earned(10);

        (await w.Service.CountAsync(Ct)).AsT0.Should().Be(0);
        await Book(w, w.P1, 2);
        await Book(w, w.P1, 3);
        (await w.Service.CountAsync(Ct)).AsT0.Should().Be(2);
    }
}
