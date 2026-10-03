using Huishoudplanner.Application.Tests.Occurrences;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Users;

namespace Huishoudplanner.Application.Tests.Points;

/// <summary>
/// The two reads of the ledger: <c>points-api.test.ts</c> (the balances and the entries). The entries are written straight into the ledger
/// so the tests do not depend on the live paths. The redemption and bonus columns of the balances are slice 4.2 and 4.3.
/// </summary>
public sealed class PointsReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly D(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static PointEntry Add(PointsWorld w, string personId, string day, int amount, PointEntryKind kind = PointEntryKind.Execution, string title = "Taak")
    {
        var date = OccurrenceWorld.At(day);
        var occurrenceId = w.Ledger.NextId();
        var entry = new PointEntry(w.Ledger.NextId(), kind == PointEntryKind.Execution ? "execution:" + occurrenceId : kind + ":" + w.Ledger.NextId(), kind, personId, amount, date, date,
            null, kind == PointEntryKind.Execution ? occurrenceId : null, null, title, PointEntrySource.Live, null, null, null, OccurrenceWorld.Now, OccurrenceWorld.Now);
        w.Ledger.Items.Add(entry);
        return entry;
    }

    /// <summary>The household of the Node test: two members and an admin from the world, a guest (active, no entries), a former resident (inactive, with entries) and one who left (inactive, none).</summary>
    private static (PointsWorld World, User Guest, User Former, User Gone) Household()
    {
        var w = new PointsWorld();
        // The list order is the creation order; the people of the world are created at the same moment, so give them one.
        for (var i = 0; i < w.Occ.People.Users.Count; i++)
        {
            w.Occ.People.Users[i] = w.Occ.People.Users[i] with { CreatedAt = OccurrenceWorld.Now.AddMinutes(i - 5) };
        }

        var guest = w.Occ.People.Add("Logé", minutesAfterEpoch: 10);
        var former = w.Occ.People.Add("Oud-bewoner", active: false, minutesAfterEpoch: 11);
        var gone = w.Occ.People.Add("Vertrokken", active: false, minutesAfterEpoch: 12);
        Add(w, w.Occ.P1.Id, "2026-09-01", 3);
        Add(w, w.Occ.P1.Id, "2026-09-14", 5);
        Add(w, w.Occ.P1.Id, "2026-09-14", 2);
        Add(w, w.Occ.P1.Id, "2026-09-20", 1);
        Add(w, w.Occ.P2.Id, "2026-09-15", 10);
        Add(w, former.Id, "2026-08-30", 4);
        return (w, guest, former, gone);
    }

    private static PersonBalance Balance(string personId, long points, int executions) =>
        new(personId, points, points, 0, null, executions, 0);

    // ---- balances

    [Fact]
    public async Task Balances_sumTheWholeLedgerInTheOrderOfTheUserListIncludingInactivePeopleWithEntries()
    {
        var (w, guest, former, gone) = Household();

        var balances = (await w.Service.BalancesAsync(null, null, Ct)).AsT0;

        (balances.From, balances.To, balances.CurrencyCode, balances.CentsPerPoint).Should().Be((null, null, "EUR", 0));
        // Persoon 1, Persoon 2, Beheerder (active, 0), Logé (active, 0), Oud-bewoner (inactive, with entries); Vertrokken has none.
        balances.Balances.Should().Equal(
            Balance(w.Occ.P1.Id, 11, 4),
            Balance(w.Occ.P2.Id, 10, 1),
            Balance(w.Occ.Admin.Id, 0, 0),
            Balance(guest.Id, 0, 0),
            Balance(former.Id, 4, 1));
        balances.Balances.Select(b => b.PersonId).Should().NotContain(gone.Id);
    }

    [Fact]
    public async Task Balances_limitTheSumToTheRangeBothDaysIncluded()
    {
        var (w, guest, _, _) = Household();

        var balances = (await w.Service.BalancesAsync(D("2026-09-14"), D("2026-09-20"), Ct)).AsT0;

        (balances.From, balances.To).Should().Be((D("2026-09-14"), D("2026-09-20")));
        balances.Balances.Should().Equal(Balance(w.Occ.P1.Id, 8, 3), Balance(w.Occ.P2.Id, 10, 1), Balance(w.Occ.Admin.Id, 0, 0), Balance(guest.Id, 0, 0));
    }

    [Fact]
    public async Task Balances_acceptASingleBound()
    {
        var (w, _, former, _) = Household();

        var onlyTo = (await w.Service.BalancesAsync(null, D("2026-09-01"), Ct)).AsT0;
        var onlyFrom = (await w.Service.BalancesAsync(D("2026-09-16"), null, Ct)).AsT0;

        onlyTo.Balances.Single(b => b.PersonId == w.Occ.P1.Id).Should().Be(Balance(w.Occ.P1.Id, 3, 1));
        onlyTo.Balances.Single(b => b.PersonId == former.Id).Points.Should().Be(4);
        onlyFrom.Balances.Single(b => b.PersonId == w.Occ.P1.Id).Should().Be(Balance(w.Occ.P1.Id, 1, 1));
        onlyFrom.Balances.Select(b => b.PersonId).Should().NotContain(former.Id);
    }

    [Fact]
    public async Task Balances_ofAnEmptyRangeAreZeroForEveryActivePerson()
    {
        var (w, guest, _, _) = Household();

        var balances = (await w.Service.BalancesAsync(D("2027-01-01"), D("2027-01-31"), Ct)).AsT0;

        balances.Balances.Select(b => b.PersonId).Should().Equal(w.Occ.P1.Id, w.Occ.P2.Id, w.Occ.Admin.Id, guest.Id);
        balances.Balances.Should().OnlyContain(b => b.Points == 0 && b.Executions == 0);
    }

    [Fact]
    public async Task Balances_aRangeThatRunsBackwardsIsFromAfterToOnFrom()
    {
        var (w, _, _, _) = Household();

        var result = await w.Service.BalancesAsync(D("2026-09-20"), D("2026-09-14"), Ct);

        result.AsT1.Errors["from"].Should().Equal("from_after_to");
    }

    [Fact]
    public async Task Balances_carryMoneyAtTheFactorInForceNowAndSplitEarnedFromRedeemed()
    {
        var (w, _, _, _) = Household();
        w.Occ.SettingsStore.Document = w.Occ.SettingsStore.Document! with { CentsPerPoint = 5, CurrencyCode = "USD" };
        Add(w, w.Occ.P1.Id, "2026-09-18", -6, PointEntryKind.Redemption);
        Add(w, w.Occ.P1.Id, "2026-09-19", 10, PointEntryKind.BonusWeekDone);

        var balances = (await w.Service.BalancesAsync(null, null, Ct)).AsT0;

        (balances.CurrencyCode, balances.CentsPerPoint).Should().Be(("USD", 5));
        balances.Balances[0].Should().Be(new PersonBalance(w.Occ.P1.Id, 15, 21, 6, new BalanceMoney(105, 30, 75), 4, 10));
    }

    [Fact]
    public async Task Balances_withoutSettingsAreSettingsMissing()
    {
        var (w, _, _, _) = Household();
        w.Occ.SettingsStore.Document = null;

        (await w.Service.BalancesAsync(null, null, Ct)).IsT2.Should().BeTrue();
    }

    [Fact]
    public async Task Balances_readEveryPageOfTheUserList()
    {
        var w = new PointsWorld();
        for (var i = 0; i < 205; i++)
        {
            w.Occ.People.Add("Extra " + i, minutesAfterEpoch: 100 + i);
        }

        var balances = (await w.Service.BalancesAsync(null, null, Ct)).AsT0;

        balances.Balances.Should().HaveCount(205 + 3);
    }

    // ---- entries

    private static PointEntriesRequest Request(PointsWorld w, string person, string from, string to, int? limit = null, string? cursor = null) =>
        new(person, D(from), D(to), limit, cursor);

    [Fact]
    public async Task Entries_listThePersonsEntriesNewestDateFirstThenByIdAsDayKeys()
    {
        var (w, _, _, _) = Household();

        var list = (await w.Service.EntriesAsync(Request(w, w.Occ.P1.Id, "2026-09-01", "2026-09-20"), Ct)).AsT0;

        list.Items.Select(v => (v.Date, v.Entry.Amount)).Should().Equal((D("2026-09-20"), 1), (D("2026-09-14"), 5), (D("2026-09-14"), 2), (D("2026-09-01"), 3));
        string.CompareOrdinal(list.Items[1].Entry.Id, list.Items[2].Entry.Id).Should().BeLessThan(0, "on the same date the earlier id comes first");
        list.Items[0].Should().BeEquivalentTo(new { WeekStart = D("2026-09-20"), PeriodStart = (DateOnly?)null });
        list.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task Entries_includeBothBoundsLeaveOtherPeopleOutAndWorkForAnInactivePerson()
    {
        var (w, guest, former, _) = Household();

        (await w.Service.EntriesAsync(Request(w, w.Occ.P1.Id, "2026-09-14", "2026-09-14"), Ct)).AsT0.Items.Select(v => v.Entry.Amount).Should().Equal(5, 2);
        (await w.Service.EntriesAsync(Request(w, w.Occ.P2.Id, "2026-09-01", "2026-09-30"), Ct)).AsT0.Items.Should().ContainSingle();
        (await w.Service.EntriesAsync(Request(w, former.Id, "2026-08-01", "2026-08-31"), Ct)).AsT0.Items.Select(v => v.Entry.Amount).Should().Equal(4);
        (await w.Service.EntriesAsync(Request(w, guest.Id, "2026-08-01", "2026-09-30"), Ct)).AsT0.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Entries_acceptARangeOfExactly371DaysAndRejectOneDayMoreWithRangeTooLargeOnTo()
    {
        var (w, _, _, _) = Household();

        (await w.Service.EntriesAsync(Request(w, w.Occ.P1.Id, "2026-01-01", "2027-01-06"), Ct)).IsT0.Should().BeTrue();
        var tooLarge = await w.Service.EntriesAsync(Request(w, w.Occ.P1.Id, "2026-01-01", "2027-01-07"), Ct);

        tooLarge.AsT1.Errors["to"].Should().Equal("range_too_large");
    }

    [Fact]
    public async Task Entries_aRangeThatRunsBackwardsIsFromAfterToOnFrom()
    {
        var (w, _, _, _) = Household();

        var result = await w.Service.EntriesAsync(Request(w, w.Occ.P1.Id, "2026-09-20", "2026-09-14"), Ct);

        result.AsT1.Errors["from"].Should().Equal("from_after_to");
    }

    [Fact]
    public async Task Entries_anInvalidPersonIdLimitOrCursorIsAFieldError()
    {
        var (w, _, _, _) = Household();

        (await w.Service.EntriesAsync(Request(w, "nope", "2026-09-01", "2026-09-20"), Ct)).AsT1.Errors["personId"].Should().Equal("invalid_object_id");
        (await w.Service.EntriesAsync(Request(w, w.Occ.P1.Id, "2026-09-01", "2026-09-20", limit: 0), Ct)).AsT1.Errors.Should().ContainKey("limit");
        (await w.Service.EntriesAsync(Request(w, w.Occ.P1.Id, "2026-09-01", "2026-09-20", limit: 501), Ct)).AsT1.Errors.Should().ContainKey("limit");
        (await w.Service.EntriesAsync(Request(w, w.Occ.P1.Id, "2026-09-01", "2026-09-20", cursor: "garbage"), Ct)).AsT1.Errors["cursor"].Should().Equal("invalid_cursor");
    }

    [Fact]
    public async Task Entries_arePagedWithACursorThatContinuesWhereThePageEndedEvenInsideOneDate()
    {
        var (w, _, _, _) = Household();
        var seen = new List<int>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = (await w.Service.EntriesAsync(Request(w, w.Occ.P1.Id, "2026-09-01", "2026-09-30", limit: 1, cursor: cursor), Ct)).AsT0;
            seen.AddRange(page.Items.Select(v => v.Entry.Amount));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        seen.Should().Equal(1, 5, 2, 3);
        pages.Should().Be(4);
    }

    [Fact]
    public async Task Entries_withoutSettingsAreSettingsMissing()
    {
        var (w, _, _, _) = Household();
        w.Occ.SettingsStore.Document = null;

        (await w.Service.EntriesAsync(Request(w, w.Occ.P1.Id, "2026-09-01", "2026-09-20"), Ct)).IsT2.Should().BeTrue();
    }

    [Fact]
    public async Task Entries_readsOfAFailingStoreArePortErrors()
    {
        var (w, _, _, _) = Household();
        w.Ledger.Failure = new PortError("pointEntries.failed: boom");

        (await w.Service.EntriesAsync(Request(w, w.Occ.P1.Id, "2026-09-01", "2026-09-20"), Ct)).IsT3.Should().BeTrue();
        (await w.Service.BalancesAsync(null, null, Ct)).IsT3.Should().BeTrue();
    }
}
