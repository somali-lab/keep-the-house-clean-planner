using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Users;

namespace Huishoudplanner.Domain.Tests.Points;

/// <summary>The rules of reading the ledger: the range, the cursor, the names of the kinds and the balances of the household.</summary>
public sealed class PointsReadingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    private static DateOnly D(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static User Person(string id, bool active = true) =>
        new(id, "Naam " + id, "#000000", active, Role.Member, [], UserDefaults.NewUserMinutes, UserDefaults.NewUserMinutes, BrowserNotifications.Disabled, Now, Now);

    private static HouseholdSettings Settings(int? cents = null, string? currency = null) =>
        SettingsDefaults.ForNewInstallation("Europe/Amsterdam", D("2026-09-14"), Now) with { CentsPerPoint = cents, CurrencyCode = currency };

    // ---- the range

    [Fact]
    public void CheckRange_aRangeThatRunsBackwardsIsFromAfterToOnFrom() =>
        PointsRules.CheckRange(D("2026-09-20"), D("2026-09-14")).Should().ContainKey("from").WhoseValue.Should().Equal("from_after_to");

    [Theory]
    [InlineData("2026-09-14", "2026-09-14")]
    [InlineData("2026-09-14", "2026-09-20")]
    public void CheckRange_aRangeOfOneDayOrMoreIsFine(string from, string to) =>
        PointsRules.CheckRange(D(from), D(to)).Should().BeEmpty();

    [Fact]
    public void CheckRange_aMissingBoundIsFine()
    {
        PointsRules.CheckRange(null, D("2026-09-14")).Should().BeEmpty();
        PointsRules.CheckRange(D("2026-09-14"), null).Should().BeEmpty();
        PointsRules.CheckRange(null, null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("2026-01-01", "2027-01-06", true)] // 371 days, both included
    [InlineData("2026-01-01", "2027-01-07", false)] // 372 days
    public void CheckEntries_theRangeIsAtMost371DaysBothDaysIncluded(string from, string to, bool accepted)
    {
        var errors = PointsRules.CheckEntries(new PointEntriesRequest("0000000000000000000000a1", D(from), D(to)));

        if (accepted)
        {
            errors.Should().BeEmpty();
        }
        else
        {
            errors["to"].Should().Equal("range_too_large");
        }
    }

    [Fact]
    public void CheckEntries_aBackwardsRangeIsReportedOnFromAndNotAlsoAsTooLarge()
    {
        var errors = PointsRules.CheckEntries(new PointEntriesRequest("0000000000000000000000a1", D("2027-12-01"), D("2026-01-01")));

        errors.Keys.Should().Equal("from");
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("0000000000000000000000a")]
    [InlineData("")]
    public void CheckEntries_aPersonIdThatIsNotAnObjectIdIsRefused(string personId) =>
        PointsRules.CheckEntries(new PointEntriesRequest(personId, D("2026-09-01"), D("2026-09-02"))).Should().ContainKey("personId");

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void CheckEntries_theLimitIsFrom1To500(int limit, bool accepted) =>
        PointsRules.CheckEntries(new PointEntriesRequest("0000000000000000000000a1", D("2026-09-01"), D("2026-09-02"), limit)).ContainsKey("limit").Should().Be(!accepted);

    // ---- the cursor

    [Fact]
    public void Cursor_roundTrips()
    {
        var cursor = new PointEntryCursor(new DateTimeOffset(2026, 9, 14, 22, 0, 0, TimeSpan.Zero), "0000000000000000000000e1");

        PointEntryCursor.TryDecode(cursor.Encode(), out var decoded).Should().BeTrue();

        decoded.Should().Be(cursor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("W10")] // []
    [InlineData("WzEsMl0")] // [1,2]
    [InlineData("WzEsIm5vcGUiXQ")] // [1,"nope"]
    public void Cursor_anythingThisApplicationDidNotProduceIsRefused(string? value) =>
        PointEntryCursor.TryDecode(value, out _).Should().BeFalse();

    [Fact]
    public void Cursor_aTooLongValueIsRefused() =>
        PointEntryCursor.TryDecode(new string('A', 101), out _).Should().BeFalse();

    // ---- the names of the kinds

    [Theory]
    [InlineData(PointEntryKind.Execution, "execution")]
    [InlineData(PointEntryKind.BonusWeekDone, "bonus_week_done")]
    [InlineData(PointEntryKind.BonusWeekOnTime, "bonus_week_ontime")]
    [InlineData(PointEntryKind.BonusCycleDone, "bonus_cycle_done")]
    [InlineData(PointEntryKind.BonusCycleOnTime, "bonus_cycle_ontime")]
    [InlineData(PointEntryKind.Redemption, "redemption")]
    public void Kind_hasTheWireNameOfTheNodeServerAndParsesBack(PointEntryKind kind, string wire)
    {
        PointNames.ToWire(kind).Should().Be(wire);
        PointNames.TryParseKind(wire, out var parsed).Should().BeTrue();
        parsed.Should().Be(kind);
    }

    [Theory]
    [InlineData("Execution")]
    [InlineData("tip")]
    [InlineData(null)]
    public void Kind_anUnknownNameIsNotAKind(string? wire) => PointNames.TryParseKind(wire, out _).Should().BeFalse();

    [Theory]
    [InlineData(PointEntrySource.Live, "live")]
    [InlineData(PointEntrySource.Backfill, "backfill")]
    [InlineData(PointEntrySource.Recompute, "recompute")]
    public void Source_hasTheWireNameOfTheNodeServerAndParsesBack(PointEntrySource source, string wire)
    {
        PointNames.ToWire(source).Should().Be(wire);
        PointNames.TryParseSource(wire, out var parsed).Should().BeTrue();
        parsed.Should().Be(source);
    }

    // ---- the balances of the household

    [Fact]
    public void BuildBalances_listsEveryActivePersonAlsoAtZeroAndAnInactiveOneOnlyWithEntries()
    {
        var users = new[] { Person("a1"), Person("a2", active: false), Person("a3", active: false), Person("a4") };
        var totals = new[] { new PointTotal("a1", 11, 4, 0, 0), new PointTotal("a2", 4, 1, 0, 0) };

        var balances = PointsRules.BuildBalances(null, null, Settings(), users, totals);

        balances.Balances.Select(b => (b.PersonId, b.Points, b.Executions)).Should().Equal(("a1", 11, 4), ("a2", 4, 1), ("a4", 0, 0));
    }

    [Fact]
    public void BuildBalances_theCurrencyAndTheFactorDefaultToEuroAndZeroAndMoneyIsNullThen()
    {
        var balances = PointsRules.BuildBalances(null, null, Settings(), [Person("a1")], [new PointTotal("a1", 11, 4, 0, 0)]);

        (balances.CurrencyCode, balances.CentsPerPoint).Should().Be(("EUR", 0));
        balances.Balances[0].Money.Should().BeNull();
    }

    [Fact]
    public void BuildBalances_moneyIsWholeCentsAtTheFactorInForceNowForEarnedRedeemedAndTheBalance()
    {
        var totals = new[] { new PointTotal("a1", 15, 4, 10, 6) };

        var balance = PointsRules.BuildBalances(D("2026-09-01"), D("2026-09-30"), Settings(5, "USD"), [Person("a1")], totals).Balances[0];

        balance.Should().Be(new PersonBalance("a1", 15, 21, 6, new BalanceMoney(105, 30, 75), 4, 10));
    }

    [Fact]
    public void BuildBalances_theBalanceCanBeNegativeWhenRedeemedWorkWasUndone()
    {
        var balance = PointsRules.BuildBalances(null, null, Settings(10), [Person("a1")], [new PointTotal("a1", -5, 0, 0, 5)]).Balances[0];

        (balance.Points, balance.Earned, balance.Money!.Balance).Should().Be((-5, 0, -50));
    }

    [Fact]
    public void BuildBalances_theRangeIsReportedBack()
    {
        var balances = PointsRules.BuildBalances(D("2026-09-14"), null, Settings(), [], []);

        (balances.From, balances.To).Should().Be((D("2026-09-14"), null));
    }

    // ---- the day keys of an entry

    [Fact]
    public void EntryView_showsTheDatesAsDayKeysInTheHouseholdTimezone()
    {
        var zone = Huishoudplanner.Domain.Calendar.DayKeys.FindZone("Europe/Amsterdam");
        var date = new DateTimeOffset(2026, 9, 13, 22, 0, 0, TimeSpan.Zero); // midnight Monday 14 September in Amsterdam
        var entry = new PointEntry("e1", "execution:x", PointEntryKind.BonusWeekDone, "a1", 10, date.AddDays(6), date, date, null, null, string.Empty, PointEntrySource.Recompute, null, null, null, Now, Now);

        var view = PointEntryView.From(entry, zone);

        (view.Date, view.WeekStart, view.PeriodStart).Should().Be((D("2026-09-20"), D("2026-09-14"), D("2026-09-14")));
    }
}
