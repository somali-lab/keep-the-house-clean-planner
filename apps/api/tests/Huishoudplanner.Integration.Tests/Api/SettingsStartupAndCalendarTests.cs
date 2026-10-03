using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

using Huishoudplanner.Integration.Tests.Fixtures;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// The settings document at startup (seed.test.ts: the settings part) and the calendar on top of it: <c>ForReadingCycleAnchor</c> is
/// now the Mongo adapter, so the calendar answers from the stored anchor and <c>settings_missing</c> without one.
/// </summary>
public sealed class SettingsStartupAndCalendarTests(MongoContainerFixture mongo)
{
    [Fact]
    public async Task Startup_createsTheSettingsOfAFreshInstallation_withAnAuditedCreateOfTheSystemActor()
    {
        await using var h = await SettingsHarness.StartAsync(mongo, now: "2026-09-16T08:00:00Z");

        var stored = await h.StoredSettings();

        stored["cycleAnchorDate"].AsString.Should().Be("2026-09-14", "the cycle starts on the Monday of the week of the first start");
        stored["weekStartsOn"].AsInt32.Should().Be(1);
        stored["timezone"].AsString.Should().Be("Europe/Amsterdam");
        stored["vacationRanges"].AsBsonArray.Should().BeEmpty();
        stored["intervals"].AsBsonArray.Select(i => i["key"].AsString).Should().Equal("daily", "3w", "2w", "1w", "2wk", "4wk", "quarter");
        stored["aiProvider"].AsBsonDocument.ShouldBeBson(new BsonDocument { { "type", "none" } });
        stored["completionControl"].AsString.Should().Be("circle");
        stored["aiPrompts"].AsBsonDocument["planProposal"].AsString.Should().StartWith("Maak een praktisch vierwekenplan");
        stored["promoteThreshold"].AsInt32.Should().Be(2);
        stored["dismissedPromotions"].AsBsonArray.Should().BeEmpty();
        stored.Names.Should().NotContain(["bonusSchedule", "bonusFloor", "currencyCode", "centsPerPoint", "rewardGoals", "aiPromptTemplates"]);
        stored["createdAt"].ToUniversalTime().Should().Be(new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));
        var entry = (await h.AuditEntries("create")).Should().ContainSingle().Subject;
        entry["actorId"].AsObjectId.ToString().Should().Be("000000000000000000000000");
        entry["source"].AsString.Should().Be("system");
        entry["entityId"].AsObjectId.ToString().Should().Be("000000000000000000000001");
        entry["before"].AsBsonDocument.ElementCount.Should().Be(0);
        entry["after"].AsBsonDocument["cycleAnchorDate"].AsString.Should().Be("2026-09-14");
    }

    [Fact]
    public async Task Startup_takesTheTimezoneFromConfiguration()
    {
        await using var h = await SettingsHarness.StartAsync(mongo, configure: f => f.WithSetting("TZ_APP", "UTC"));

        (await h.StoredSettings())["timezone"].AsString.Should().Be("UTC");
    }

    [Fact]
    public async Task Startup_isIdempotent_andLeavesExistingSettingsAlone()
    {
        var existing = new BsonDocument
        {
            { "_id", new ObjectId("000000000000000000000001") },
            { "cycleAnchorDate", "2026-01-05" },
            { "weekStartsOn", 1 },
            { "timezone", "Europe/Amsterdam" },
            { "vacationRanges", new BsonArray() },
            { "intervals", new BsonArray { new BsonDocument { { "key", "3w" }, { "label", "3x per week" }, { "perCycle", 12 }, { "periodDays", 2 } } } },
            { "aiProvider", new BsonDocument { { "type", "mock" } } },
            { "promoteThreshold", 4 },
            { "dismissedPromotions", new BsonArray() },
            { "createdAt", new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) },
            { "updatedAt", new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) },
        };
        await using var h = await SettingsHarness.StartAsync(
            mongo, prepare: db => db.GetCollection<BsonDocument>("settings").InsertOneAsync(existing, cancellationToken: TestContext.Current.CancellationToken));

        (await h.StoredSettings()).ShouldBeBson(existing);
        (await h.AuditEntries(action: null)).Should().BeEmpty();
    }

    [Fact]
    public async Task Startup_addsTheShippedThreePerWeekInterval_toAnOlderInstallation()
    {
        var older = new BsonDocument
        {
            { "_id", new ObjectId("000000000000000000000001") },
            { "cycleAnchorDate", "2026-01-05" },
            { "weekStartsOn", 1 },
            { "timezone", "Europe/Amsterdam" },
            { "vacationRanges", new BsonArray() },
            {
                "intervals",
                new BsonArray
                {
                    new BsonDocument { { "key", "daily" }, { "label", "Dagelijks" }, { "perCycle", 28 }, { "periodDays", 1 } },
                    new BsonDocument { { "key", "2w" }, { "label", "2x per week" }, { "perCycle", 8 }, { "periodDays", 3 } },
                }
            },
            { "aiProvider", new BsonDocument { { "type", "none" } } },
            { "promoteThreshold", 2 },
            { "dismissedPromotions", new BsonArray() },
            { "someFieldOfANewerVersion", "kept" },
            { "createdAt", new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) },
            { "updatedAt", new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) },
        };
        await using var h = await SettingsHarness.StartAsync(
            mongo, prepare: db => db.GetCollection<BsonDocument>("settings").InsertOneAsync(older, cancellationToken: TestContext.Current.CancellationToken));

        var stored = await h.StoredSettings();

        stored["intervals"].AsBsonArray.Select(i => i["key"].AsString).Should().Equal("daily", "3w", "2w");
        stored["someFieldOfANewerVersion"].AsString.Should().Be("kept");
        var entry = (await h.AuditEntries()).Should().ContainSingle().Subject;
        entry["source"].AsString.Should().Be("system");
    }

    [Fact]
    public async Task Calendar_answersFromTheStoredAnchor()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Get("/api/v2/calendar?from=2026-10-11&to=2026-10-12");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var days = (await SettingsHarness.Body(response)).GetProperty("days");
        days[0].GetProperty("dayKey").GetString().Should().Be("2026-10-11");
        days[0].GetProperty("cycleIndex").GetInt32().Should().Be(0);
        days[0].GetProperty("weekIndex").GetInt32().Should().Be(3);
        days[1].GetProperty("cycleIndex").GetInt32().Should().Be(1);
        days[1].GetProperty("weekIndex").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Calendar_followsAChangeOfTheAnchor()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        (await h.Patch("""{ "cycleAnchorDate": "2026-10-05" }""")).StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await SettingsHarness.Body(await h.Get("/api/v2/calendar?from=2026-10-11&to=2026-10-11"));

        var day = body.GetProperty("days")[0];
        (day.GetProperty("cycleIndex").GetInt32(), day.GetProperty("weekIndex").GetInt32()).Should().Be((0, 0), "11 October is the Sunday of the first week of a cycle that starts on 5 October");
    }

    [Fact]
    public async Task Calendar_withoutASettingsDocument_isSettingsMissing()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        await h.SettingsCollection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, TestContext.Current.CancellationToken);

        var response = await h.Get("/api/v2/calendar?from=2026-10-11&to=2026-10-12");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await SettingsHarness.Body(response)).GetProperty("type").GetString().Should().Be("urn:huishoudplanner:problem:settings_missing");
    }
}
