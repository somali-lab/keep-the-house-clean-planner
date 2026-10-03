using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

using Huishoudplanner.Integration.Tests.Fixtures;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports the first <c>describe</c> of apps/server/test/settings.test.ts (the settings API) through the real pipeline against a
/// real replica set, plus the cases the Node suite leaves to the shared schema tests (currency, cents per point, reward goals) and
/// the v2 rules (the API key is never returned, defaults are delivered, the startup seed).
/// </summary>
public sealed class SettingsEndpointTests(MongoContainerFixture mongo)
{
    private static readonly string[] DefaultIntervalKeys = ["daily", "3w", "2w", "1w", "2wk", "4wk", "quarter"];

    private static async Task<JsonElement> ProblemOf(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await SettingsHarness.Body(response);
        body.GetProperty("type").GetString().Should().Be($"urn:huishoudplanner:problem:{code}");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
        return body;
    }

    private static string[] ErrorsOf(JsonElement problem, string field) =>
        [.. problem.GetProperty("errors").GetProperty(field).EnumerateArray().Select(e => e.GetString()!)];

    // ---- GET -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_returnsTheSeededSettings_withoutAProfile()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Get();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await SettingsHarness.Body(response);
        body.GetProperty("id").GetString().Should().Be("000000000000000000000001");
        body.GetProperty("cycleAnchorDate").GetString().Should().Be("2026-09-14");
        body.GetProperty("weekStartsOn").GetInt32().Should().Be(1);
        body.GetProperty("timezone").GetString().Should().Be("Europe/Amsterdam");
        body.GetProperty("intervals").EnumerateArray().Select(i => i.GetProperty("key").GetString()).Should().Equal(DefaultIntervalKeys);
        body.GetProperty("intervals")[6].GetProperty("perCycle").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("aiProvider").GetProperty("type").GetString().Should().Be("none");
        body.GetProperty("completionControl").GetString().Should().Be("circle");
        body.GetProperty("promoteThreshold").GetInt32().Should().Be(2);
        body.GetProperty("dismissedPromotions").GetArrayLength().Should().Be(0);
        body.GetProperty("createdAt").GetDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-09-16T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Get_alwaysDeliversTheDefaults_forWhatIsNotStored()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var body = await h.SettingsBody();

        body.GetProperty("bonusSchedule").GetArrayLength().Should().Be(0);
        body.GetProperty("bonusesInForce").GetProperty("weekDone").GetInt32().Should().Be(0);
        body.GetProperty("currencyCode").GetString().Should().Be("EUR");
        body.GetProperty("centsPerPoint").GetInt32().Should().Be(0);
        body.GetProperty("rewardGoals").GetProperty("weekPoints").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("rewardGoals").GetProperty("cyclePoints").ValueKind.Should().Be(JsonValueKind.Null);
        var stored = await h.StoredSettings();
        stored.Contains("bonusSchedule").Should().BeFalse("the defaults are delivered, not written");
        stored.Contains("currencyCode").Should().BeFalse();
    }

    [Fact]
    public async Task Get_withoutASettingsDocument_isSettingsMissing()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        await h.SettingsCollection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, TestContext.Current.CancellationToken);

        var problem = await ProblemOf(await h.Get(), HttpStatusCode.InternalServerError, "settings_missing");

        problem.TryGetProperty("keys", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Patch_withoutASettingsDocument_isSettingsMissing_andWritesNothing()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        await h.SettingsCollection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, TestContext.Current.CancellationToken);

        await ProblemOf(await h.Patch("""{ "promoteThreshold": 3 }"""), HttpStatusCode.InternalServerError, "settings_missing");

        (await h.AuditEntries(action: null)).Where(e => e["action"] == "update").Should().BeEmpty();
        (await h.SettingsCollection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: TestContext.Current.CancellationToken)).Should().Be(0);
    }

    // ---- who may write -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Patch_requiresAnAdministrator()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        var anonymous = await h.Send(HttpMethod.Patch, "/api/v2/settings", null, """{ "promoteThreshold": 3 }""");
        await ProblemOf(anonymous, HttpStatusCode.BadRequest, "profile_required");

        await ProblemOf(await h.Patch("""{ "promoteThreshold": 3 }""", h.Member), HttpStatusCode.Forbidden, "permission_denied");
        await ProblemOf(await h.Patch("""{ "promoteThreshold": 3 }""", h.Planner), HttpStatusCode.Forbidden, "permission_denied");

        (await h.StoredSettings())["promoteThreshold"].AsInt32.Should().Be(2);
    }

    // ---- ported from settings.test.ts ----------------------------------------------------------------------------

    [Fact]
    public async Task Patch_rejectsAnAnchorThatIsNotAMonday()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var problem = await ProblemOf(await h.Patch("""{ "cycleAnchorDate": "2026-09-15" }"""), HttpStatusCode.BadRequest, "validation_error");

        ErrorsOf(problem, "cycleAnchorDate").Should().Equal("anchor_not_monday");
    }

    [Fact]
    public async Task Patch_updatesTheAnchor_withAnAuditedDiff()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Patch("""{ "cycleAnchorDate": "2026-09-07" }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SettingsHarness.Body(response)).GetProperty("cycleAnchorDate").GetString().Should().Be("2026-09-07");
        var entry = (await h.AuditEntries()).Should().ContainSingle().Subject;
        entry["entity"].AsString.Should().Be("settings");
        entry["entityId"].AsObjectId.ToString().Should().Be("000000000000000000000001");
        entry["actorId"].AsObjectId.ToString().Should().Be(h.Admin);
        entry["source"].AsString.Should().Be("ui");
        entry["before"].AsBsonDocument.ShouldBeBson(new BsonDocument { { "cycleAnchorDate", "2026-09-14" } });
        entry["after"].AsBsonDocument.ShouldBeBson(new BsonDocument { { "cycleAnchorDate", "2026-09-07" } });
        (await h.StoredSettings())["cycleAnchorDate"].AsString.Should().Be("2026-09-07");
    }

    [Fact]
    public async Task Patch_managesVacationRanges_andRejectsInvertedOnes()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var ok = await h.Patch("""{ "vacationRanges": [{ "from": "2026-12-24", "to": "2026-12-31" }] }""");

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = (await h.AuditEntries()).Should().ContainSingle().Subject;
        entry["after"].AsBsonDocument.ShouldBeBson(new BsonDocument
        {
            { "vacationRanges", new BsonArray { new BsonDocument { { "from", "2026-12-24" }, { "to", "2026-12-31" } } } },
        });
        var inverted = await h.Patch("""{ "vacationRanges": [{ "from": "2026-12-31", "to": "2026-12-24" }] }""");
        var problem = await ProblemOf(inverted, HttpStatusCode.BadRequest, "validation_error");
        ErrorsOf(problem, "vacationRanges.0.to").Should().Equal("vacation_range_inverted");
    }

    [Fact]
    public async Task Patch_addsANewInterval_thatTasksCanUse_withoutCodeChanges()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Patch(WithIntervals(includeQuarter: true, includeYear: true));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var year = (await SettingsHarness.Body(response)).GetProperty("intervals").EnumerateArray().Last();
        year.GetProperty("key").GetString().Should().Be("year");
        year.GetProperty("label").GetString().Should().Be("1x per jaar");
        year.GetProperty("perCycle").ValueKind.Should().Be(JsonValueKind.Null);
        year.GetProperty("periodDays").GetInt32().Should().Be(365);
        (await h.AuditEntries()).Should().ContainSingle();
        (await h.StoredSettings())["intervals"].AsBsonArray.Last().AsBsonDocument["perCycle"].IsBsonNull.Should().BeTrue("an interval without a count per cycle holds an explicit null, as the Node server writes it");
    }

    [Fact]
    public async Task Patch_allowsRemovingAnUnusedInterval_butBlocksRemovingOneInUse()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        (await h.Patch(WithIntervals(includeQuarter: true, includeYear: true))).StatusCode.Should().Be(HttpStatusCode.OK);
        await h.Database.GetCollection<BsonDocument>("tasks").InsertOneAsync(
            new BsonDocument { { "name", "Matras keren" }, { "intervalKey", "year" }, { "active", true } }, cancellationToken: TestContext.Current.CancellationToken);

        // 'quarter' is unused, so dropping it is fine; 'year' is used by the task above.
        var ok = await h.Patch(WithIntervals(includeQuarter: false, includeYear: true));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var before = (await h.StoredSettings())["intervals"];

        var blocked = await h.Patch(WithIntervals(includeQuarter: false, includeYear: false));

        var problem = await ProblemOf(blocked, HttpStatusCode.Conflict, "interval_in_use");
        problem.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).Should().Equal("year");
        (await h.StoredSettings())["intervals"].Should().Be(before);
        (await h.AuditEntries()).Should().HaveCount(2, "the blocked removal wrote and audited nothing");
    }

    [Fact]
    public async Task Patch_rejectsDuplicateIntervalKeys()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        var duplicate = """{ "intervals": [{ "key": "daily", "label": "Dagelijks", "perCycle": 28, "periodDays": 1 }, { "key": "daily", "label": "Nog eens", "perCycle": null, "periodDays": 2 }] }""";

        var problem = await ProblemOf(await h.Patch(duplicate), HttpStatusCode.BadRequest, "validation_error");

        ErrorsOf(problem, "intervals").Should().Equal("duplicate_interval_key");
    }

    [Fact]
    public async Task Patch_trimsTheIntervalKeyAndLabel()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Patch("""{ "intervals": [{ "key": " week ", "label": "  Elke week ", "perCycle": 4, "periodDays": 7 }] }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "no task uses the removed intervals");
        var interval = (await SettingsHarness.Body(response)).GetProperty("intervals")[0];
        (interval.GetProperty("key").GetString(), interval.GetProperty("label").GetString()).Should().Be(("week", "Elke week"));
    }

    [Fact]
    public async Task Patch_storesAnAiTimeoutBetween10And900Seconds()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var ok = await h.Patch("""{ "aiProvider": { "type": "ollama", "endpoint": "http://host.docker.internal:11434", "model": "qwen3:8b", "timeoutSeconds": 240 } }""");

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var provider = (await SettingsHarness.Body(ok)).GetProperty("aiProvider");
        provider.GetProperty("type").GetString().Should().Be("ollama");
        provider.GetProperty("endpoint").GetString().Should().Be("http://host.docker.internal:11434");
        provider.GetProperty("model").GetString().Should().Be("qwen3:8b");
        provider.GetProperty("timeoutSeconds").GetInt32().Should().Be(240);
        foreach (var timeout in new[] { 9, 901 })
        {
            var response = await h.Patch($$"""{ "aiProvider": { "type": "ollama", "model": "qwen3:8b", "timeoutSeconds": {{timeout}} } }""");
            await ProblemOf(response, HttpStatusCode.BadRequest, "validation_error");
        }
    }

    [Fact]
    public async Task Patch_neverStoresOrReturnsAnApiKey()
    {
        const string key = "sk-test-secret-value";
        await using var h = await SettingsHarness.StartAsync(mongo, configure: f => f.WithSetting("AI_API_KEY", key));

        var response = await h.Patch($$"""{ "aiProvider": { "type": "anthropic", "model": "claude-test", "apiKey": "{{key}}" }, "apiKey": "{{key}}" }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain(key);
        (await (await h.Get()).Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain(key);
        (await h.StoredSettings()).ToJson().Should().NotContain(key);
        (await h.AuditLog.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(TestContext.Current.CancellationToken)).Select(e => e.ToJson()).Should().NotContain(j => j.Contains(key));
    }

    [Fact]
    public async Task Patch_storesPromptTemplates_onlyWhenThePlaceholdersRemain()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        const string action = """{ "system": "Systeem {{schema}}", "user": "Gebruiker {{input}}" }""";
        var templates = $$"""{ "planProposal": {{action}}, "planRebalance": {{action}}, "taskSuggestions": {{action}}, "planExplanation": {{action}} }""";

        var ok = await h.Patch($$"""{ "aiPromptTemplates": {{templates}} }""");

        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var stored = (await SettingsHarness.Body(ok)).GetProperty("aiPromptTemplates");
        stored.GetProperty("planProposal").GetProperty("system").GetString().Should().Be("Systeem {{schema}}");
        var invalid = $$$"""{ "aiPromptTemplates": { "planProposal": { "system": "zonder schema", "user": "Gebruiker {{input}}" }, "planRebalance": {{{action}}}, "taskSuggestions": {{{action}}}, "planExplanation": {{{action}}} } }""";
        var problem = await ProblemOf(await h.Patch(invalid), HttpStatusCode.BadRequest, "validation_error");
        ErrorsOf(problem, "aiPromptTemplates.planProposal.system").Should().Equal("schema_placeholder_required");
    }

    [Fact]
    public async Task Patch_doesNotWriteOrAudit_whenNothingChanges()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);
        var before = await h.StoredSettings();
        h.Clock.Set("2026-09-17T08:00:00Z");

        var response = await h.Patch("""{ "promoteThreshold": 2 }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.AuditEntries()).Should().BeEmpty();
        (await h.StoredSettings()).ShouldBeBson(before, "not even updatedAt moved");
    }

    [Fact]
    public async Task Patch_withAnEmptyBody_isANoOp()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Patch("{}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.AuditEntries()).Should().BeEmpty();
    }

    [Fact]
    public async Task Patch_ignoresWhatClientsMayNotSet()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Patch("""{ "timezone": "UTC", "weekStartsOn": 7, "dismissedPromotions": [], "bonusFloor": "2026-01-01", "unknown": 1 }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await h.StoredSettings();
        stored["timezone"].AsString.Should().Be("Europe/Amsterdam");
        stored["weekStartsOn"].AsInt32.Should().Be(1);
        stored.Contains("bonusFloor").Should().BeFalse();
        (await h.AuditEntries()).Should().BeEmpty();
    }

    [Fact]
    public async Task Patch_changesSeveralFieldsInOneAuditEntry()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Patch("""{ "completionControl": "thumb", "promoteThreshold": 3, "aiPrompts": { "planProposal": "Maak een plan.", "planRebalance": "", "taskSuggestions": "", "planExplanation": "" } }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = (await h.AuditEntries()).Should().ContainSingle().Subject;
        entry["before"].AsBsonDocument["completionControl"].AsString.Should().Be("circle");
        entry["after"].AsBsonDocument["completionControl"].AsString.Should().Be("thumb");
        entry["after"].AsBsonDocument["promoteThreshold"].AsInt32.Should().Be(3);
        entry["after"].AsBsonDocument["aiPrompts"].AsBsonDocument["planProposal"].AsString.Should().Be("Maak een plan.");
        entry.Contains("meta").Should().BeFalse();
    }

    // ---- shape problems ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "promoteThreshold": "3" }""", "promoteThreshold", "expected_integer")]
    [InlineData("""{ "promoteThreshold": 1.5 }""", "promoteThreshold", "expected_integer")]
    [InlineData("""{ "promoteThreshold": 1 }""", "promoteThreshold", "out_of_range")]
    [InlineData("""{ "cycleAnchorDate": 5 }""", "cycleAnchorDate", "expected_string")]
    [InlineData("""{ "cycleAnchorDate": "2026-02-30" }""", "cycleAnchorDate", "invalid_day_key")]
    [InlineData("""{ "vacationRanges": {} }""", "vacationRanges", "expected_array")]
    [InlineData("""{ "vacationRanges": [{ "from": "2026-12-24" }] }""", "vacationRanges.0.to", "required")]
    [InlineData("""{ "completionControl": "pinch" }""", "completionControl", "invalid_enum")]
    [InlineData("""{ "aiProvider": { "type": "gpt" } }""", "aiProvider.type", "invalid_enum")]
    [InlineData("""{ "aiProvider": { "model": "x" } }""", "aiProvider.type", "required")]
    [InlineData("""{ "aiProvider": { "type": "ollama", "endpoint": "not a url" } }""", "aiProvider.endpoint", "invalid")]
    [InlineData("""{ "aiProvider": { "type": "ollama", "model": "" } }""", "aiProvider.model", "out_of_range")]
    [InlineData("""{ "aiPrompts": { "planProposal": "x" } }""", "aiPrompts.planRebalance", "required")]
    [InlineData("""{ "intervals": [{ "key": "", "label": "x", "perCycle": null, "periodDays": 1 }] }""", "intervals.0.key", "out_of_range")]
    [InlineData("""{ "intervals": [{ "key": "x", "label": "x", "periodDays": 1 }] }""", "intervals.0.perCycle", "required")]
    [InlineData("""{ "intervals": [{ "key": "x", "label": "x", "perCycle": 0, "periodDays": 1 }] }""", "intervals.0.perCycle", "out_of_range")]
    [InlineData("""{ "intervals": [{ "key": "x", "label": "x", "perCycle": null, "periodDays": 0 }] }""", "intervals.0.periodDays", "out_of_range")]
    public async Task Patch_reportsEveryShapeAndRuleProblemWithItsFieldPath(string body, string field, string message)
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var problem = await ProblemOf(await h.Patch(body), HttpStatusCode.BadRequest, "validation_error");

        ErrorsOf(problem, field).Should().Contain(message);
        (await h.AuditEntries()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public async Task Patch_withABodyThatIsNotAJsonObject_isAValidationError(string body)
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Send(HttpMethod.Patch, "/api/v2/settings", h.Admin, body.Length == 0 ? null : body);

        await ProblemOf(response, HttpStatusCode.BadRequest, "validation_error");
    }

    // ---- currency, cents per point, reward goals (requirements 4.12) ----------------------------------------------

    [Fact]
    public async Task Patch_setsTheCurrencyAndTheCentsPerPoint_asOneAuditedUpdate()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Patch("""{ "currencyCode": "USD", "centsPerPoint": 5 }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await SettingsHarness.Body(response);
        (body.GetProperty("currencyCode").GetString(), body.GetProperty("centsPerPoint").GetInt32()).Should().Be(("USD", 5));
        var entry = (await h.AuditEntries()).Should().ContainSingle().Subject;
        entry["after"].AsBsonDocument.ShouldBeBson(new BsonDocument { { "currencyCode", "USD" }, { "centsPerPoint", 5 } });
        var again = await h.Patch("""{ "currencyCode": "USD", "centsPerPoint": 5 }""");
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.AuditEntries()).Should().ContainSingle("the same values again write and audit nothing");
        (await h.Patch("""{ "centsPerPoint": 0 }""")).StatusCode.Should().Be(HttpStatusCode.OK);
        var back = (await h.AuditEntries()).Last();
        back["before"].AsBsonDocument.ShouldBeBson(new BsonDocument { { "centsPerPoint", 5 } });
        back["after"].AsBsonDocument.ShouldBeBson(new BsonDocument { { "centsPerPoint", 0 } });
    }

    [Fact]
    public async Task Patch_withTheDefaultCurrencyAndCents_whenNoneAreStored_writesAndAuditsNothing()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Patch("""{ "currencyCode": "EUR", "centsPerPoint": 0, "rewardGoals": { "weekPoints": null, "cyclePoints": null } }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.AuditEntries()).Should().BeEmpty();
        (await h.StoredSettings()).Contains("currencyCode").Should().BeFalse();
    }

    [Theory]
    [InlineData("XXX", "invalid_currency_code")]
    [InlineData("eur", "invalid_currency_code")]
    [InlineData("EURO", "invalid_currency_code")]
    [InlineData("JPY", "currency_not_two_decimals")]
    [InlineData("KWD", "currency_not_two_decimals")]
    public async Task Patch_refusesACurrencyThatIsNotAKnownTwoDecimalCurrency(string code, string message)
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var problem = await ProblemOf(await h.Patch($$"""{ "currencyCode": "{{code}}" }"""), HttpStatusCode.BadRequest, "validation_error");

        ErrorsOf(problem, "currencyCode").Should().Equal(message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    [InlineData(1.5)]
    public async Task Patch_refusesCentsPerPointOutsideTheRangeOrFractional(double cents)
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        await ProblemOf(await h.Patch($$"""{ "centsPerPoint": {{cents.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }"""), HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Patch_setsTheRewardGoals_withExplicitNullsForTheAutomaticGoal()
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var response = await h.Patch("""{ "rewardGoals": { "weekPoints": 100, "cyclePoints": null } }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var goals = (await SettingsHarness.Body(response)).GetProperty("rewardGoals");
        goals.GetProperty("weekPoints").GetInt32().Should().Be(100);
        goals.GetProperty("cyclePoints").ValueKind.Should().Be(JsonValueKind.Null);
        (await h.StoredSettings())["rewardGoals"].AsBsonDocument.ShouldBeBson(new BsonDocument { { "weekPoints", 100 }, { "cyclePoints", BsonNull.Value } });
        (await h.Patch("""{ "rewardGoals": { "weekPoints": 100, "cyclePoints": null } }""")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.AuditEntries()).Should().ContainSingle("goals equal to the ones in force write and audit nothing");
    }

    [Theory]
    [InlineData("""{ "rewardGoals": { "weekPoints": 100 } }""", "rewardGoals.cyclePoints", "required")]
    [InlineData("""{ "rewardGoals": { "weekPoints": -1, "cyclePoints": null } }""", "rewardGoals.weekPoints", "out_of_range")]
    [InlineData("""{ "rewardGoals": { "weekPoints": null, "cyclePoints": 100001 } }""", "rewardGoals.cyclePoints", "out_of_range")]
    [InlineData("""{ "rewardGoals": { "weekPoints": "x", "cyclePoints": null } }""", "rewardGoals.weekPoints", "expected_integer")]
    public async Task Patch_refusesIncompleteOrOutOfRangeRewardGoals(string body, string field, string message)
    {
        await using var h = await SettingsHarness.StartAsync(mongo);

        var problem = await ProblemOf(await h.Patch(body), HttpStatusCode.BadRequest, "validation_error");

        ErrorsOf(problem, field).Should().Contain(message);
    }

    private static string WithIntervals(bool includeQuarter, bool includeYear)
    {
        var intervals = new List<string>
        {
            """{ "key": "daily", "label": "Dagelijks", "perCycle": 28, "periodDays": 1 }""",
            """{ "key": "3w", "label": "3x per week", "perCycle": 12, "periodDays": 2 }""",
            """{ "key": "2w", "label": "2x per week", "perCycle": 8, "periodDays": 3 }""",
            """{ "key": "1w", "label": "1x per week", "perCycle": 4, "periodDays": 7 }""",
            """{ "key": "2wk", "label": "1x per 2 weken", "perCycle": 2, "periodDays": 14 }""",
            """{ "key": "4wk", "label": "1x per 4 weken", "perCycle": 1, "periodDays": 28 }""",
        };
        if (includeQuarter)
        {
            intervals.Add("""{ "key": "quarter", "label": "1x per kwartaal", "perCycle": null, "periodDays": 91 }""");
        }

        if (includeYear)
        {
            intervals.Add("""{ "key": "year", "label": "1x per jaar", "perCycle": null, "periodDays": 365 }""");
        }

        return $$"""{ "intervals": [{{string.Join(", ", intervals)}}] }""";
    }
}
