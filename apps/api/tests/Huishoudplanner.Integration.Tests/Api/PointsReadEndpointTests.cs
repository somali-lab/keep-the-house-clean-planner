using System.Net;
using System.Text.Json;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// <c>points-api.test.ts</c> on <c>/api/v2/points</c>: the balances and the entries read the ledger. The entries are written straight into the
/// database, so the tests do not depend on the live paths. Real HTTP pipeline and real MongoDB replica set; one database per test.
/// </summary>
public sealed class PointsReadEndpointTests(MongoContainerFixture mongo)
{
    private static string Type(JsonElement problem) => problem.GetProperty("type").GetString()!;

    /// <summary>The household of the Node test: the profiles of the harness plus a guest (active, no entries), a former resident (inactive, with entries) and one who left (inactive, none).</summary>
    private static async Task<(ObjectId Guest, ObjectId Former, ObjectId Gone)> SeedAsync(PointsHarness h)
    {
        var (guest, former, gone) = (ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), ObjectId.GenerateNewId());
        await h.AddUserAsync(guest, "Logé", "member", true);
        await h.AddUserAsync(former, "Oud-bewoner", "member", false);
        await h.AddUserAsync(gone, "Vertrokken", "member", false);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-01", 3);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 5);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 2);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-20", 1);
        await h.InsertExecutionEntryAsync(h.P2, "2026-09-15", 10);
        await h.InsertExecutionEntryAsync(former, "2026-08-30", 4);
        return (guest, former, gone);
    }

    private static (string Person, long Points, long Earned, long Redeemed, int Executions, long Bonus)[] Rows(JsonElement body) =>
        [.. body.GetProperty("balances").EnumerateArray().Select(b => (
            b.GetProperty("personId").GetString()!,
            b.GetProperty("points").GetInt64(),
            b.GetProperty("earned").GetInt64(),
            b.GetProperty("redeemed").GetInt64(),
            b.GetProperty("executions").GetInt32(),
            b.GetProperty("bonusPoints").GetInt64()))];

    // ---- balances

    [Fact]
    public async Task Balances_sumTheWholeLedgerInTheOrderOfTheUserListIncludingInactivePeopleWithEntries()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var (guest, former, gone) = await SeedAsync(h);

        var (status, body) = await h.GetAsync("/api/v2/points/balances");

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("from").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("to").ValueKind.Should().Be(JsonValueKind.Null);
        (body.GetProperty("currencyCode").GetString(), body.GetProperty("centsPerPoint").GetInt32()).Should().Be(("EUR", 0));
        // Beheerder, Persoon 1, Persoon 2, Logé (active, 0), Oud-bewoner (inactive, with entries); Vertrokken has none.
        Rows(body).Should().Equal(
            (h.Admin.ToString(), 0, 0, 0, 0, 0),
            (h.P1.ToString(), 11, 11, 0, 4, 0),
            (h.P2.ToString(), 10, 10, 0, 1, 0),
            (guest.ToString(), 0, 0, 0, 0, 0),
            (former.ToString(), 4, 4, 0, 1, 0));
        Rows(body).Select(r => r.Person).Should().NotContain(gone.ToString());
        body.GetProperty("balances")[1].GetProperty("money").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Balances_limitTheSumToTheRangeBothDaysIncludedAndReportTheRange()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var (guest, _, _) = await SeedAsync(h);

        var (status, body) = await h.GetAsync("/api/v2/points/balances?from=2026-09-14&to=2026-09-20");

        status.Should().Be(HttpStatusCode.OK);
        (body.GetProperty("from").GetString(), body.GetProperty("to").GetString()).Should().Be(("2026-09-14", "2026-09-20"));
        Rows(body).Should().Equal(
            (h.Admin.ToString(), 0, 0, 0, 0, 0),
            (h.P1.ToString(), 8, 8, 0, 3, 0),
            (h.P2.ToString(), 10, 10, 0, 1, 0),
            (guest.ToString(), 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task Balances_acceptASingleBoundAndNeedNoProfile()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var (_, former, _) = await SeedAsync(h);

        var onlyTo = Rows((await h.GetAsync("/api/v2/points/balances?to=2026-09-01")).Body);
        var onlyFrom = Rows((await h.GetAsync("/api/v2/points/balances?from=2026-09-16")).Body);

        onlyTo.Single(r => r.Person == h.P1.ToString()).Should().Be((h.P1.ToString(), 3, 3, 0, 1, 0));
        onlyTo.Single(r => r.Person == former.ToString()).Points.Should().Be(4);
        onlyFrom.Single(r => r.Person == h.P1.ToString()).Should().Be((h.P1.ToString(), 1, 1, 0, 1, 0));
        onlyFrom.Select(r => r.Person).Should().NotContain(former.ToString());
    }

    [Fact]
    public async Task Balances_ofAnEmptyRangeAreZeroForEveryActivePerson()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var (guest, _, _) = await SeedAsync(h);

        var (status, body) = await h.GetAsync("/api/v2/points/balances?from=2027-01-01&to=2027-01-31");

        status.Should().Be(HttpStatusCode.OK);
        Rows(body).Select(r => r.Person).Should().Equal(h.Admin.ToString(), h.P1.ToString(), h.P2.ToString(), guest.ToString());
        Rows(body).Should().OnlyContain(r => r.Points == 0 && r.Executions == 0);
    }

    [Theory]
    [InlineData("?from=2026-09-20&to=2026-09-14", "from", "from_after_to")]
    [InlineData("?from=14-09-2026", "from", "invalid_day_key")]
    [InlineData("?to=2026-02-30", "to", "invalid_day_key")]
    public async Task Balances_aBadRangeIsAFieldValidationError(string query, string field, string message)
    {
        await using var h = await PointsHarness.StartAsync(mongo);

        var (status, body) = await h.GetAsync("/api/v2/points/balances" + query);

        status.Should().Be(HttpStatusCode.BadRequest);
        Type(body).Should().Be("urn:huishoudplanner:problem:validation_error");
        body.GetProperty("errors").GetProperty(field).EnumerateArray().Select(e => e.GetString()).Should().Equal(message);
    }

    [Fact]
    public async Task Balances_carryTheMoneyOfEarnedRedeemedAndTheBalanceAtTheFactorInForceNow()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 20);
        await h.InsertOtherEntryAsync(h.P1, "2026-09-15", 10, "bonus_week_done");
        await h.InsertOtherEntryAsync(h.P1, "2026-09-16", -6, "redemption");
        var patch = await h.SendAsync(HttpMethod.Patch, "/api/v2/settings", h.Admin, new { centsPerPoint = 5 });
        patch.Status.Should().Be(HttpStatusCode.OK, patch.Body.ToString());

        var (_, body) = await h.GetAsync("/api/v2/points/balances");

        var mine = body.GetProperty("balances").EnumerateArray().Single(b => b.GetProperty("personId").GetString() == h.P1.ToString());
        (mine.GetProperty("points").GetInt64(), mine.GetProperty("earned").GetInt64(), mine.GetProperty("redeemed").GetInt64()).Should().Be((24, 30, 6));
        (mine.GetProperty("executions").GetInt32(), mine.GetProperty("bonusPoints").GetInt64()).Should().Be((1, 10));
        var money = mine.GetProperty("money");
        (money.GetProperty("earned").GetInt64(), money.GetProperty("redeemed").GetInt64(), money.GetProperty("balance").GetInt64()).Should().Be((150, 30, 120));
        body.GetProperty("centsPerPoint").GetInt32().Should().Be(5);
    }

    // ---- entries

    private static string Entries(ObjectId person, string from, string to, string extra = "") =>
        $"/api/v2/points/entries?personId={person}&from={from}&to={to}{extra}";

    [Fact]
    public async Task Entries_listThePersonsEntriesNewestDateFirstThenByIdAsDayKeys()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await SeedAsync(h);

        var (status, body) = await h.GetAsync(Entries(h.P1, "2026-09-01", "2026-09-20"));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Select(e => (e.GetProperty("date").GetString(), e.GetProperty("amount").GetInt32())).Should().Equal(
            ("2026-09-20", 1), ("2026-09-14", 5), ("2026-09-14", 2), ("2026-09-01", 3));
        // Same date: the earlier id comes first.
        string.CompareOrdinal(items[1].GetProperty("id").GetString(), items[2].GetProperty("id").GetString()).Should().BeLessThan(0);
        body.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        var first = items[0];
        first.GetProperty("id").GetString().Should().MatchRegex("^[0-9a-f]{24}$");
        first.GetProperty("key").GetString().Should().MatchRegex("^execution:[0-9a-f]{24}$");
        (first.GetProperty("kind").GetString(), first.GetProperty("personId").GetString(), first.GetProperty("weekStart").GetString()).Should().Be(("execution", h.P1.ToString(), "2026-09-14"));
        first.GetProperty("periodStart").ValueKind.Should().Be(JsonValueKind.Null);
        first.GetProperty("occurrenceId").GetString().Should().MatchRegex("^[0-9a-f]{24}$");
        first.GetProperty("taskId").ValueKind.Should().Be(JsonValueKind.Null);
        (first.GetProperty("titleSnapshot").GetString(), first.GetProperty("source").GetString()).Should().Be(("Taak", "live"));
        foreach (var nullable in new[] { "note", "centsPerPointSnapshot", "currencyCodeSnapshot" })
        {
            first.GetProperty(nullable).ValueKind.Should().Be(JsonValueKind.Null, nullable);
        }

        first.GetProperty("createdAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));
        first.GetProperty("updatedAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Entries_includeBothBoundsLeaveOtherPeopleOutAndWorkForAnInactivePerson()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var (guest, former, _) = await SeedAsync(h);

        static int[] Amounts(JsonElement body) => [.. body.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("amount").GetInt32())];

        Amounts((await h.GetAsync(Entries(h.P1, "2026-09-14", "2026-09-14"))).Body).Should().Equal(5, 2);
        Amounts((await h.GetAsync(Entries(h.P2, "2026-09-01", "2026-09-30"))).Body).Should().HaveCount(1);
        Amounts((await h.GetAsync(Entries(former, "2026-08-01", "2026-08-31"))).Body).Should().Equal(4);
        Amounts((await h.GetAsync(Entries(guest, "2026-08-01", "2026-09-30"))).Body).Should().BeEmpty();
    }

    [Fact]
    public async Task Entries_listAllKindsOfEntryEachWithItsOwnFields()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await h.InsertOtherEntryAsync(h.P1, "2026-09-20", 10, "bonus_week_done");
        await h.InsertOtherEntryAsync(h.P1, "2026-09-18", -6, "redemption");

        var (_, body) = await h.GetAsync(Entries(h.P1, "2026-09-14", "2026-09-20"));

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Select(e => e.GetProperty("kind").GetString()).Should().Equal("bonus_week_done", "redemption");
        items[0].GetProperty("periodStart").GetString().Should().Be("2026-09-20");
        (items[1].GetProperty("note").GetString(), items[1].GetProperty("centsPerPointSnapshot").GetInt32(), items[1].GetProperty("currencyCodeSnapshot").GetString()).Should().Be(("Pizza", 5, "EUR"));
    }

    [Fact]
    public async Task Entries_rowsThatCannotBeMappedNeverShortenAPageOrHideTheNextCursor()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        var ct = TestContext.Current.CancellationToken;
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-14", 1);
        await h.InsertExecutionEntryAsync(h.P1, "2026-09-13", 2);
        await h.Ledger.InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", "kind-from-the-future" }, { "key", "x:1" }, { "personId", h.P1 }, { "amount", 9 }, { "date", PointsHarness.Midnight("2026-09-15") } }, cancellationToken: ct);
        await h.Ledger.InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "kind", "execution" }, { "personId", h.P1 }, { "amount", 9 }, { "date", PointsHarness.Midnight("2026-09-15") } }, cancellationToken: ct);

        var (_, page) = await h.GetAsync(Entries(h.P1, "2026-09-01", "2026-09-30", "&limit=1"));

        page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("amount").GetInt32()).Should().Equal(1);
        page.GetProperty("nextCursor").GetString().Should().NotBeNull();
    }

    [Fact]
    public async Task Entries_aRangeOfExactly371DaysIsAcceptedAndOneDayMoreIsRangeTooLargeOnTo()
    {
        await using var h = await PointsHarness.StartAsync(mongo);

        (await h.GetAsync(Entries(h.P1, "2026-01-01", "2027-01-06"))).Status.Should().Be(HttpStatusCode.OK);
        var (status, body) = await h.GetAsync(Entries(h.P1, "2026-01-01", "2027-01-07"));

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("errors").GetProperty("to").EnumerateArray().Select(e => e.GetString()).Should().Equal("range_too_large");
    }

    [Theory]
    [InlineData("?from=2026-09-01&to=2026-09-20", "personId")]
    [InlineData("?personId=nope&from=2026-09-01&to=2026-09-20", "personId")]
    [InlineData("?personId=000000000000000000000001&to=2026-09-20", "from")]
    [InlineData("?personId=000000000000000000000001&from=2026-09-01", "to")]
    [InlineData("?personId=000000000000000000000001&from=2026-09-01&to=2026-09-20&limit=x", "limit")]
    [InlineData("?personId=000000000000000000000001&from=2026-09-01&to=2026-09-20&limit=501", "limit")]
    [InlineData("?personId=000000000000000000000001&from=2026-09-01&to=2026-09-20&cursor=garbage", "cursor")]
    public async Task Entries_aMissingOrMalformedParameterIsAFieldValidationError(string query, string field)
    {
        await using var h = await PointsHarness.StartAsync(mongo);

        var (status, body) = await h.GetAsync("/api/v2/points/entries" + query);

        status.Should().Be(HttpStatusCode.BadRequest);
        Type(body).Should().Be("urn:huishoudplanner:problem:validation_error");
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Contain(field);
    }

    [Fact]
    public async Task Entries_aRangeThatRunsBackwardsIsFromAfterToOnFrom()
    {
        await using var h = await PointsHarness.StartAsync(mongo);

        var (status, body) = await h.GetAsync(Entries(h.P1, "2026-09-20", "2026-09-14"));

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("errors").GetProperty("from").EnumerateArray().Select(e => e.GetString()).Should().Equal("from_after_to");
    }

    [Fact]
    public async Task Entries_arePagedWithACursorThatContinuesInsideOneDate()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await SeedAsync(h);

        var amounts = new List<int>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var (status, body) = await h.GetAsync(Entries(h.P1, "2026-09-01", "2026-09-30", "&limit=1" + (cursor is null ? string.Empty : "&cursor=" + cursor)));
            status.Should().Be(HttpStatusCode.OK);
            amounts.AddRange(body.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("amount").GetInt32()));
            cursor = body.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        amounts.Should().Equal(1, 5, 2, 3);
        pages.Should().Be(4);
    }

    [Fact]
    public async Task TheReads_needNoProfileAndWriteNothing()
    {
        await using var h = await PointsHarness.StartAsync(mongo);
        await SeedAsync(h);
        var audits = await h.AuditCountAsync();
        var entries = await h.Ledger.CountDocumentsAsync(new BsonDocument(), cancellationToken: TestContext.Current.CancellationToken);

        (await h.GetAsync("/api/v2/points/balances")).Status.Should().Be(HttpStatusCode.OK);
        (await h.GetAsync(Entries(h.P1, "2026-09-01", "2026-09-30"))).Status.Should().Be(HttpStatusCode.OK);

        (await h.AuditCountAsync(), await h.Ledger.CountDocumentsAsync(new BsonDocument(), cancellationToken: TestContext.Current.CancellationToken)).Should().Be((audits, entries));
    }
}
