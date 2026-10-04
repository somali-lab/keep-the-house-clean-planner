using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

using Huishoudplanner.Integration.Tests.Fixtures;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports the second <c>describe</c> of apps/server/test/settings.test.ts (period bonuses, ADR-0012) and adds what v2 delivers:
/// <c>bonusesInForce</c> and <c>startsInFuture</c>. The Node unit test of the compare-and-set on the stored schedule has no
/// counterpart: the read, the check and the write are one transaction here (see the concurrency test).
/// </summary>
public sealed class SettingsBonusEndpointTests(MongoContainerFixture mongo)
{
    private const string Amounts = """{ "weekDone": 5, "weekOnTime": 3, "cycleDone": 20, "cycleOnTime": 10 }""";

    private static string Bonuses(string amounts) => $$"""{ "periodBonuses": {{amounts}} }""";

    private static async Task<JsonElement[]> Schedule(SettingsHarness h) =>
        [.. (await h.SettingsBody()).GetProperty("bonusSchedule").EnumerateArray()];

    private static (string From, int WeekDone, int WeekOnTime, int CycleDone, int CycleOnTime) Row(JsonElement row) => (
        row.GetProperty("from").GetString()!,
        row.GetProperty("weekDone").GetInt32(),
        row.GetProperty("weekOnTime").GetInt32(),
        row.GetProperty("cycleDone").GetInt32(),
        row.GetProperty("cycleOnTime").GetInt32());

    [Fact]
    public async Task Get_returnsAnEmptySchedule_soBonusesAreDisabled()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        (await Schedule(h)).Should().BeEmpty();
        (await h.StoredSettings()).Contains("bonusSchedule").Should().BeFalse();
    }

    [Fact]
    public async Task Patch_writesAndAuditsNothing_forAmountsThatEqualTheOnesInForce_alsoWhenAllAreZero()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        var before = await h.StoredSettings();

        var response = await h.Patch(Bonuses("""{ "weekDone": 0, "weekOnTime": 0, "cycleDone": 0, "cycleOnTime": 0 }"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.AuditEntries()).Should().BeEmpty();
        (await h.StoredSettings()).ShouldBeBson(before);
    }

    [Fact]
    public async Task Patch_writesARowFromToday_withAnAuditedBeforeAndAfter_forAdministratorsOnly()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var denied = await h.Patch(Bonuses(Amounts), h.Member);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Schedule(h)).Should().BeEmpty();

        var response = await h.Patch(Bonuses(Amounts));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await SettingsHarness.Body(response);
        Row(body.GetProperty("bonusSchedule")[0]).Should().Be(("2026-09-16", 5, 3, 20, 10));
        var entry = (await h.AuditEntries()).Should().ContainSingle().Subject;
        entry["source"].AsString.Should().Be("ui");
        entry["before"].AsBsonDocument.ShouldBeBson(new BsonDocument { { "bonusSchedule", new BsonArray() } });
        entry["after"].AsBsonDocument.ShouldBeBson(new BsonDocument
        {
            { "bonusSchedule", new BsonArray { new BsonDocument { { "from", "2026-09-16" }, { "weekDone", 5 }, { "weekOnTime", 3 }, { "cycleDone", 20 }, { "cycleOnTime", 10 } } } },
        });
        (await h.StoredSettings())["bonusSchedule"].AsBsonArray.Should().HaveCount(1);
    }

    [Fact]
    public async Task Patch_writesNothingForTheSameAmountsAgain_andReplacesTheRowThatStartsToday()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        (await h.Patch(Bonuses(Amounts))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await h.Patch(Bonuses(Amounts))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.AuditEntries()).Should().ContainSingle();

        var changed = await h.Patch(Bonuses("""{ "weekDone": 6, "weekOnTime": 3, "cycleDone": 20, "cycleOnTime": 10 }"""));

        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await h.AuditEntries();
        entries.Should().HaveCount(2);
        entries[1]["before"].AsBsonDocument["bonusSchedule"].AsBsonArray[0].AsBsonDocument["weekDone"].AsInt32.Should().Be(5);
        (await Schedule(h)).Select(Row).Should().Equal(("2026-09-16", 6, 3, 20, 10));
    }

    [Fact]
    public async Task Patch_keepsTheEarlierRows_andAddsARowOnALaterDay_soAnEndedPeriodKeepsItsAmounts()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        (await h.Patch(Bonuses(Amounts))).StatusCode.Should().Be(HttpStatusCode.OK);
        h.Clock.Set("2026-09-30T08:00:00Z");

        (await h.Patch(Bonuses("""{ "weekDone": 1, "weekOnTime": 2, "cycleDone": 3, "cycleOnTime": 4 }"""))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Schedule(h)).Select(Row).Should().Equal(("2026-09-16", 5, 3, 20, 10), ("2026-09-30", 1, 2, 3, 4));
        // Switching a kind off is an amount of 0, which is also a row.
        (await h.Patch(Bonuses("""{ "weekDone": 1, "weekOnTime": 2, "cycleDone": 3, "cycleOnTime": 0 }"""))).StatusCode.Should().Be(HttpStatusCode.OK);
        Row((await Schedule(h))[1]).Should().Be(("2026-09-30", 1, 2, 3, 0));
    }

    [Theory]
    [InlineData("""{ "weekDone": 1001, "weekOnTime": 3, "cycleDone": 20, "cycleOnTime": 10 }""")]
    [InlineData("""{ "weekDone": -1, "weekOnTime": 3, "cycleDone": 20, "cycleOnTime": 10 }""")]
    [InlineData("""{ "weekDone": 5, "weekOnTime": 3, "cycleDone": 20, "cycleOnTime": 1.5 }""")]
    [InlineData("""{ "weekDone": 1 }""")]
    public async Task Patch_rejectsAmountsOutside0To1000_andIncompleteOrFractionalAmounts(string amounts)
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Patch(Bonuses(amounts));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SettingsHarness.Body(response)).GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:validation_error");
        (await h.AuditEntries()).Should().BeEmpty();
    }

    [Fact]
    public async Task Patch_acceptsTheMaximumAmount_andNeverTakesTheScheduleItselfFromAClient()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        (await h.Patch(Bonuses("""{ "weekDone": 1000, "weekOnTime": 3, "cycleDone": 20, "cycleOnTime": 10 }"""))).StatusCode.Should().Be(HttpStatusCode.OK);

        var direct = await h.Patch("""{ "bonusSchedule": [{ "from": "2026-01-01", "weekDone": 5, "weekOnTime": 3, "cycleDone": 20, "cycleOnTime": 10 }] }""");

        direct.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Schedule(h)).Select(r => r.GetProperty("from").GetString()).Should().Equal("2026-09-16");
    }

    [Fact]
    public async Task Get_namesTheAmountsInForceToday_andFlagsRowsThatStartLater()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        (await h.Patch(Bonuses(Amounts))).StatusCode.Should().Be(HttpStatusCode.OK);
        // A row that starts in the future (an administrator cannot write one, an import or the Node app could).
        await h.SettingsCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Empty,
            Builders<BsonDocument>.Update.Push("bonusSchedule", new BsonDocument { { "from", "2026-10-15" }, { "weekDone", 9 }, { "weekOnTime" , 9 }, { "cycleDone", 9 }, { "cycleOnTime", 9 } }),
            cancellationToken: TestContext.Current.CancellationToken);

        var body = await h.SettingsBody();

        body.GetProperty("bonusSchedule").EnumerateArray().Select(r => r.GetProperty("startsInFuture").GetBoolean()).Should().Equal(false, true);
        var inForce = body.GetProperty("bonusesInForce");
        (inForce.GetProperty("weekDone").GetInt32(), inForce.GetProperty("cycleOnTime").GetInt32()).Should().Be((5, 10));
        h.Clock.Set("2026-10-15T08:00:00Z");
        var later = await h.SettingsBody();
        later.GetProperty("bonusSchedule").EnumerateArray().Select(r => r.GetProperty("startsInFuture").GetBoolean()).Should().Equal(false, false);
        later.GetProperty("bonusesInForce").GetProperty("weekDone").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task Get_usesTheHouseholdTimezoneForToday()
    {
        // 23:30 UTC is already the next day in Amsterdam: the row of the 17th is in force.
        await using var h = await SettingsHarness.StartAsync(mongo);
        (await h.Patch(Bonuses(Amounts))).StatusCode.Should().Be(HttpStatusCode.OK);
        h.Clock.Set("2026-09-16T23:30:00Z");
        (await h.Patch(Bonuses("""{ "weekDone": 1, "weekOnTime": 2, "cycleDone": 3, "cycleOnTime": 4 }"""))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Schedule(h)).Select(r => r.GetProperty("from").GetString()).Should().Equal("2026-09-16", "2026-09-17");
    }

    [Fact]
    public async Task ConcurrentAdministrators_neverLeaveMoreThanOneRowForToday_andEveryAnswerIs200Or409()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var answers = await Task.WhenAll(Enumerable.Range(1, 6).Select(weekDone =>
            h.Patch(Bonuses($$"""{ "weekDone": {{weekDone}}, "weekOnTime": 3, "cycleDone": 20, "cycleOnTime": 10 }"""))));

        var statuses = answers.Select(a => a.StatusCode).ToList();
        // Every writer sends the ETag it read, so the losers are told 412 before the schedule compare-and-set is reached (ADR-0022).
        statuses.Should().OnlyContain(s => s == HttpStatusCode.OK || s == HttpStatusCode.Conflict || s == HttpStatusCode.PreconditionFailed);
        statuses.Should().Contain(HttpStatusCode.OK);
        foreach (var conflict in answers.Where(a => a.StatusCode == HttpStatusCode.Conflict))
        {
            (await SettingsHarness.Body(conflict)).GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:bonus_schedule_conflict");
        }

        var schedule = (await h.StoredSettings())["bonusSchedule"].AsBsonArray;
        schedule.Should().HaveCount(1);
        schedule[0].AsBsonDocument["from"].AsString.Should().Be("2026-09-16");
        // One audit entry per write that changed something; a write of the amounts that were already in force left none.
        (await h.AuditEntries()).Count.Should().BeInRange(1, 6);
    }
}
