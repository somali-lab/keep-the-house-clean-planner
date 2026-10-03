using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Points;

namespace Huishoudplanner.Domain.Tests.Points;

/// <summary>The rules of booking and undoing a redemption and how it appears in the audit log (requirements 4.12; <c>domain/redemptions.ts</c>).</summary>
public sealed class RedemptionRulesTests
{
    private const string Person = "0000000000000000000000a1";
    private const string Other = "0000000000000000000000a2";
    private const string EntryId = "0000000000000000000000e1";
    private const string Key = "redeem-key-aaaaaaaaaaaa";

    private static readonly TimeZoneInfo Zone = DayKeys.FindZone("Europe/Amsterdam");
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day = DayKeys.FromDayKey(new DateOnly(2026, 9, 16), Zone);
    private static readonly DateTimeOffset Monday = DayKeys.FromDayKey(new DateOnly(2026, 9, 14), Zone);

    private static Actor Member(string id = Person) => new(id, Role.Member, ActorSource.Ui);

    private static Actor Admin(string id = Other) => new(id, Role.Admin, ActorSource.Ui);

    private static PointEntry Redemption(string person = Person, int amount = -4, string? note = "Pizza", DateTimeOffset? date = null, int? cents = 25, string? currency = "EUR") =>
        new(EntryId, "redemption:" + EntryId, PointEntryKind.Redemption, person, amount, date ?? Day, Monday, null, null, null, string.Empty, PointEntrySource.Live, note, cents, currency, Now, Now);

    // ---- validate

    [Fact]
    public void Validate_acceptsABookingWithOnlyPoints() =>
        RedemptionRules.Validate(new RedemptionCommand(null, 1, null, null)).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Validate_refusesPointsBelowOneOnPoints(int points) =>
        RedemptionRules.Validate(new RedemptionCommand(null, points, null, null))!.Errors.Keys.Should().Equal("points");

    [Fact]
    public void Validate_countsTheNoteTrimmedAndAllowsExactly200Characters()
    {
        RedemptionRules.Validate(new RedemptionCommand(null, 1, new string('x', 200), null)).Should().BeNull();
        RedemptionRules.Validate(new RedemptionCommand(null, 1, "  " + new string('x', 200) + "  ", null)).Should().BeNull();
        RedemptionRules.Validate(new RedemptionCommand(null, 1, new string('x', 201), null))!.Errors.Keys.Should().Equal("note");
    }

    [Theory]
    [InlineData("kort")]
    [InlineData("has spaces in the key")]
    public void Validate_refusesARequestKeyThatIsNoKeyOnRequestId(string key) =>
        RedemptionRules.Validate(new RedemptionCommand(null, 1, null, key))!.Errors["requestId"].Should().Equal("invalid_request_key");

    [Fact]
    public void Validate_refusesAPersonThatIsNoIdOnPersonId() =>
        RedemptionRules.Validate(new RedemptionCommand("nope", 1, null, null))!.Errors["personId"].Should().Equal("invalid_object_id");

    [Fact]
    public void Validate_reportsEveryFieldAtOnce() =>
        RedemptionRules.Validate(new RedemptionCommand("nope", 0, new string('x', 201), "kort"))!.Errors.Keys.Should().BeEquivalentTo("personId", "points", "note", "requestId");

    // ---- the note

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Ijsje  ", "Ijsje")]
    public void NormalizeNote_trimsAndTreatsEmptyAsNone(string? note, string? expected) =>
        RedemptionRules.NormalizeNote(note).Should().Be(expected);

    // ---- replay

    [Fact]
    public void IsReplay_isTrueOnlyForTheSamePersonPointsAndNote()
    {
        var stored = Redemption();

        RedemptionRules.IsReplay(stored, Person, 4, "Pizza").Should().BeTrue();
        RedemptionRules.IsReplay(stored, Person, 5, "Pizza").Should().BeFalse();
        RedemptionRules.IsReplay(stored, Person, 4, "Andere").Should().BeFalse();
        RedemptionRules.IsReplay(stored, Person, 4, null).Should().BeFalse();
        RedemptionRules.IsReplay(stored, Other, 4, "Pizza").Should().BeFalse();
        RedemptionRules.IsReplay(Redemption(note: null), Person, 4, null).Should().BeTrue();
    }

    // ---- who may book

    [Fact]
    public void CheckMayBook_letsAPersonBookForThemselvesAndAnAdministratorForAnyone()
    {
        RedemptionRules.CheckMayBook(Member(), Person).Should().BeNull();
        RedemptionRules.CheckMayBook(Admin(), Person).Should().BeNull();
        RedemptionRules.CheckMayBook(Member(), Other)!.Code.Should().BeNull("a refusal of the person is the plain permission_denied");
    }

    // ---- who may undo

    [Fact]
    public void CheckMayUndo_lets_theOwnerUndoOnTheSameDayUntilLocalMidnight()
    {
        RedemptionRules.CheckMayUndo(Member(), Redemption(), Zone, Now).Should().BeNull();
        // 23:59 on Wednesday in Amsterdam.
        RedemptionRules.CheckMayUndo(Member(), Redemption(), Zone, new DateTimeOffset(2026, 9, 16, 21, 59, 0, TimeSpan.Zero)).Should().BeNull();
    }

    [Fact]
    public void CheckMayUndo_locksTheOwnerOnALaterDayButNeverAnAdministrator()
    {
        // 02:30 on Thursday in Amsterdam.
        var later = new DateTimeOffset(2026, 9, 17, 0, 30, 0, TimeSpan.Zero);

        RedemptionRules.CheckMayUndo(Member(), Redemption(), Zone, later)!.Code.Should().Be(RedemptionRules.RedemptionLocked);
        RedemptionRules.CheckMayUndo(Admin(), Redemption(), Zone, later).Should().BeNull();
        RedemptionRules.CheckMayUndo(Admin(), Redemption(), Zone, new DateTimeOffset(2027, 1, 5, 9, 0, 0, TimeSpan.Zero)).Should().BeNull();
    }

    [Fact]
    public void CheckMayUndo_refusesAnotherMemberWithPermissionDeniedAlsoOnTheSameDay() =>
        RedemptionRules.CheckMayUndo(Member(Other), Redemption(), Zone, Now)!.Code.Should().BeNull();

    // ---- the conflicts

    [Fact]
    public void Insufficient_carriesTheBalanceAndTheRequestedPoints()
    {
        var conflict = RedemptionRules.Insufficient(5, 6);

        conflict.Code.Should().Be("insufficient_balance");
        conflict.Extensions.Should().BeEquivalentTo(new Dictionary<string, object?> { ["balance"] = 5L, ["requested"] = 6 });
    }

    [Fact]
    public void KeyConflict_isTheIdempotencyKeyConflict() =>
        RedemptionRules.KeyConflict().Code.Should().Be("idempotency_key_conflict");

    // ---- the draft

    [Fact]
    public void Draft_isDatedTodayInTheHouseholdTimezoneWithItsMondayAndTheFactorInForce()
    {
        var settings = Huishoudplanner.Domain.Settings.SettingsDefaults.ForNewInstallation("Europe/Amsterdam", new DateOnly(2026, 9, 14), Now) with { CentsPerPoint = 25, CurrencyCode = "USD" };
        // 00:30 on Thursday in Amsterdam is still Wednesday in UTC.
        var late = new DateTimeOffset(2026, 9, 16, 22, 30, 0, TimeSpan.Zero);

        var draft = RedemptionRules.Draft(new RedemptionCommand(null, 4, "  Pizza ", Key), Person, settings, Zone, late);

        draft.Should().Be(new NewRedemption(
            Person, 4, "Pizza", DayKeys.FromDayKey(new DateOnly(2026, 9, 17), Zone), Monday, 25, "USD", Key, late));
    }

    [Fact]
    public void Draft_usesTheDefaultsOfAnUnsetFactorAndCurrency()
    {
        var settings = Huishoudplanner.Domain.Settings.SettingsDefaults.ForNewInstallation("Europe/Amsterdam", new DateOnly(2026, 9, 14), Now);

        var draft = RedemptionRules.Draft(new RedemptionCommand(null, 1, null, null), Person, settings, Zone, Now);

        (draft.CentsPerPoint, draft.CurrencyCode, draft.Note, draft.RequestId).Should().Be((0, "EUR", null, null));
    }

    // ---- the audit entries

    [Fact]
    public void ForBooked_listsEveryFieldInAfterWithANullNoteButNeverTheRequestKey()
    {
        var entry = RedemptionAudit.ForBooked(new AuditActor(Other, AuditSource.Ui), Redemption(note: null));

        (entry.Entity, entry.Action, entry.EntityId).Should().Be((AuditEntity.Points, AuditAction.Create, EntryId));
        entry.Before.Count.Should().Be(0);
        entry.After.Keys.Should().BeEquivalentTo(
            "key", "kind", "personId", "amount", "date", "weekStart", "periodStart", "occurrenceId", "taskId", "titleSnapshot", "source", "note", "centsPerPointSnapshot", "currencyCodeSnapshot");
        entry.After["note"].Should().Be(AuditNull.Instance);
        entry.After["amount"].Should().Be(new AuditInteger(-4));
        entry.After["kind"].Should().Be(new AuditString("redemption"));
        entry.After["occurrenceId"].Should().Be(AuditNull.Instance);
        entry.Meta.Should().Be(AuditObject.Of(("reason", "redemption")));
        entry.Actor.ActorId.Should().Be(Other);
    }

    [Fact]
    public void ForBooked_keepsTheNoteAndTheFactorOfTheMoment()
    {
        var entry = RedemptionAudit.ForBooked(new AuditActor(Person, AuditSource.Ui), Redemption(note: "Pizza", cents: 0));

        entry.After["note"].Should().Be(new AuditString("Pizza"));
        entry.After["centsPerPointSnapshot"].Should().Be(new AuditInteger(0));
        entry.After["currencyCodeSnapshot"].Should().Be(new AuditString("EUR"));
    }

    [Fact]
    public void ForUndone_listsEveryFieldInBeforeAndNothingInAfterAndLeavesOutAMissingCurrency()
    {
        var entry = RedemptionAudit.ForUndone(new AuditActor(Person, AuditSource.Ui), Redemption(currency: null));

        (entry.Action, entry.After.Count).Should().Be((AuditAction.Delete, 0));
        entry.Before["note"].Should().Be(new AuditString("Pizza"));
        entry.Before.Keys.Should().NotContain("currencyCodeSnapshot");
        entry.Meta.Should().Be(AuditObject.Of(("reason", "redemption_undone")));
    }
}
