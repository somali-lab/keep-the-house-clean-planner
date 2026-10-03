#pragma warning disable CA1861 // Literal request bodies of the scenarios.

using System.Net;
using System.Text.Json;
using Huishoudplanner.Adapters.Ai;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports apps/server/test/ai-assist.test.ts: the connection test, task suggestions, plan explanations and the disabled provider, through the
/// real HTTP pipeline and a real MongoDB with the model behind <c>ForSelectingAModel</c> replaced by the deterministic mock (never a provider).
/// Adds the prompt information, roles, body validation, the 502 mapping and the stored-nothing guarantees.
/// </summary>
public sealed class AiAssistEndpointTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] DefaultIntervalKeys = ["daily", "3w", "2w", "1w", "2wk", "4wk", "quarter"];

    private static string[] Strings(JsonElement list) => [.. list.EnumerateArray().Select(e => e.GetString()!)];

    private static void ShouldBeProblem(HttpResponseMessage response, JsonElement body, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status, body.ToString());
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.GetProperty("type").GetString().Should().Be($"urn:huishoudplanner:problem:{code}");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }

    /// <summary>The Node <c>setup()</c>: a kitchen with one task and a bathroom with one.</summary>
    private static async Task<(string Keuken, string Badkamer)> TwoRoomsAsync(AiHarness h)
    {
        var keuken = await h.Room("Keuken");
        var badkamer = await h.Room("Badkamer");
        await h.Task("Keuken: aanrecht", keuken, "1w", 20);
        await h.Task("Douche", badkamer, "1w", 30);
        return (keuken, badkamer);
    }

    private static async Task SetPromptAsync(AiHarness h, string use, string text)
    {
        var prompts = new Dictionary<string, string> { ["planProposal"] = "", ["planRebalance"] = "", ["taskSuggestions"] = "", ["planExplanation"] = "", [use] = text };
        await h.Patch("/api/v2/settings", new { aiPrompts = prompts });
    }

    // ---- POST /ai/test

    [Fact]
    public async Task Test_asksTheSuppliedProviderForASmallStructuredAnswer_andStoresNothing()
    {
        await using var h = await AiHarness.StartAsync(mongo);
        var before = await h.Snapshot();

        var response = await h.Post("/api/v2/ai/test", new { aiProvider = new { type = "mock" } });

        var body = await AiHarness.Body(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        (await h.Snapshot()).Should().Be(before);
        var request = h.Request();
        request.Options.Name.Should().Be("connection-test");
        request.Messages[0].Content.Should().Be("Return {\"ok\":true}.");
        h.Models.Chosen.Should().ContainSingle().Which.Type.Should().Be(Domain.Settings.AiProviderType.Mock);
    }

    [Fact]
    public async Task Test_validatesTheSuppliedTimeout_beforeContactingTheProvider()
    {
        await using var h = await AiHarness.StartAsync(mongo);

        var response = await h.Post("/api/v2/ai/test", new { aiProvider = new { type = "ollama", model = "qwen3:8b", timeoutSeconds = 9 } });

        var body = await AiHarness.Body(response);
        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Equal("aiProvider.timeoutSeconds");
        h.Model.Requests.Should().BeEmpty();
        h.Models.Chosen.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{}", "aiProvider")]
    [InlineData("{\"aiProvider\":{}}", "aiProvider.type")]
    [InlineData("{\"aiProvider\":{\"type\":\"telepathy\"}}", "aiProvider.type")]
    [InlineData("{\"aiProvider\":{\"type\":\"ollama\",\"endpoint\":\"not a url\"}}", "aiProvider.endpoint")]
    [InlineData("{\"aiProvider\":{\"type\":\"ollama\",\"model\":\"\"}}", "aiProvider.model")]
    public async Task Test_refusesAProviderThatIsNotValid(string json, string field)
    {
        await using var h = await AiHarness.StartAsync(mongo);

        var response = await h.Send(HttpMethod.Post, "/api/v2/ai/test", json);

        var body = await AiHarness.Body(response);
        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Contain(field);
        h.Model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_reportsAnAnswerThatIsNotUsable_as502()
    {
        var responders = new Dictionary<string, MockResponder> { ["connection-test"] = MockResponder.Fixed("{\"ok\":false}") };
        await using var h = await AiHarness.StartAsync(mongo, responders);

        var response = await h.Post("/api/v2/ai/test", new { aiProvider = new { type = "mock" } });

        ShouldBeProblem(response, await AiHarness.Body(response), HttpStatusCode.BadGateway, "ai_provider_error");
    }

    [Fact]
    public async Task Test_reportsAnAnswerThatIsNotJson_as502()
    {
        var responders = new Dictionary<string, MockResponder> { ["connection-test"] = MockResponder.Fixed("hallo") };
        await using var h = await AiHarness.StartAsync(mongo, responders);

        var response = await h.Post("/api/v2/ai/test", new { aiProvider = new { type = "mock" } });

        var body = await AiHarness.Body(response);
        ShouldBeProblem(response, body, HttpStatusCode.BadGateway, "ai_provider_error");
        body.GetProperty("detail").GetString().Should().Contain("not valid JSON");
    }

    // ---- POST /ai/suggest-tasks

    [Fact]
    public async Task SuggestTasks_addsTheConfiguredPromptToTheProviderRequest()
    {
        await using var h = await AiHarness.StartAsync(mongo);
        var (keuken, _) = await TwoRoomsAsync(h);
        await SetPromptAsync(h, "taskSuggestions", "Noem vooral seizoensgebonden taken.");

        await h.Post("/api/v2/ai/suggest-tasks", new { roomId = keuken });

        h.Request().SystemPrompt.Should().Contain("Noem vooral seizoensgebonden taken.");
    }

    [Fact]
    public async Task SuggestTasks_returnsFilteredSuggestions_andStoresNothing()
    {
        await using var h = await AiHarness.StartAsync(mongo);
        var (keuken, _) = await TwoRoomsAsync(h);
        var before = await h.Snapshot();

        var response = await h.Post("/api/v2/ai/suggest-tasks", new { roomId = keuken });

        var body = await AiHarness.Body(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await h.Snapshot()).Should().Be(before);
        // The mock also returned an unknown interval key and a name that already exists in the room.
        body.GetProperty("suggestions").GetRawText().Should().Be(
            "[{\"name\":\"Keuken: plinten afnemen\",\"intervalKey\":\"4wk\",\"durationMinutes\":15,\"notes\":\"Vochtige doek.\"},"
            + "{\"name\":\"Keuken: lampen afstoffen\",\"intervalKey\":\"quarter\",\"durationMinutes\":10,\"notes\":\"\"}]");
    }

    [Fact]
    public async Task SuggestTasks_sendsTheRoom_itsExistingTasks_otherRooms_andTheKnownIntervals()
    {
        await using var h = await AiHarness.StartAsync(mongo);
        var (keuken, _) = await TwoRoomsAsync(h);

        await h.Post("/api/v2/ai/suggest-tasks", new { roomId = keuken });

        var payload = AiHarness.Payload<TaskSuggestionPayload>(h.Request());
        payload.Room.Should().Be("Keuken");
        payload.ExistingTasks.Should().BeEquivalentTo([new ExistingTaskInfo("Keuken: aanrecht", "1w", 20)]);
        payload.OtherTasks.Should().BeEquivalentTo([new OtherTaskInfo("Badkamer", "Douche")]);
        payload.Intervals.Select(i => i.Key).Should().Equal(DefaultIntervalKeys);
        h.Request().Options.Name.Should().Be("task-suggestions");
        h.Request().Options.ResponseSchema.Should().NotBeNull();
    }

    [Fact]
    public async Task SuggestTasks_dropsUnknownIntervals_invalidDurations_emptyNames_andCaseInsensitiveDuplicates()
    {
        var answer = JsonSerializer.Serialize(new
        {
            suggestions = new object[]
            {
                new { name = "Oven reinigen", intervalKey = "4wk", durationMinutes = 30 },
                new { name = "oven REINIGEN ", intervalKey = "4wk", durationMinutes = 30 },
                new { name = "KEUKEN: AANRECHT", intervalKey = "1w", durationMinutes = 10 },
                new { name = "Koelkast", intervalKey = "yearly", durationMinutes = 40 },
                new { name = "Vriezer ontdooien", intervalKey = "quarter", durationMinutes = 0 },
                new { name = "Afzuigkap", intervalKey = "4wk", durationMinutes = 12.5 },
                new { name = "   ", intervalKey = "1w", durationMinutes = 5 },
            },
        });
        await using var h = await AiHarness.StartAsync(mongo, new Dictionary<string, MockResponder> { ["task-suggestions"] = MockResponder.Fixed(answer) });
        var (keuken, _) = await TwoRoomsAsync(h);

        var response = await h.Post("/api/v2/ai/suggest-tasks", new { roomId = keuken });

        (await AiHarness.Body(response)).GetProperty("suggestions").GetRawText().Should().Be(
            "[{\"name\":\"Oven reinigen\",\"intervalKey\":\"4wk\",\"durationMinutes\":30,\"notes\":\"\"}]");
    }

    [Fact]
    public async Task SuggestTasks_reportsAnUnusableAnswerAs422_anUnknownRoomAs404_andAMissingRoomAs400()
    {
        await using var h = await AiHarness.StartAsync(mongo, new Dictionary<string, MockResponder> { ["task-suggestions"] = MockResponder.Fixed("geen json") });
        var (keuken, _) = await TwoRoomsAsync(h);

        var unusable = await h.Post("/api/v2/ai/suggest-tasks", new { roomId = keuken });
        var unknown = await h.Post("/api/v2/ai/suggest-tasks", new { roomId = "0123456789abcdef01234567" });
        var missing = await h.Post("/api/v2/ai/suggest-tasks", new { });
        var malformed = await h.Post("/api/v2/ai/suggest-tasks", new { roomId = "nope" });

        ShouldBeProblem(unusable, await AiHarness.Body(unusable), HttpStatusCode.UnprocessableEntity, "ai_invalid_response");
        ShouldBeProblem(unknown, await AiHarness.Body(unknown), HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(missing, await AiHarness.Body(missing), HttpStatusCode.BadRequest, "validation_error");
        ShouldBeProblem(malformed, await AiHarness.Body(malformed), HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task SuggestTasks_reportsJsonOfTheWrongShapeAs422_withTheErrorsListed()
    {
        await using var h = await AiHarness.StartAsync(mongo, new Dictionary<string, MockResponder> { ["task-suggestions"] = MockResponder.Fixed("{\"suggestions\":[{\"name\":1}]}") });
        var (keuken, _) = await TwoRoomsAsync(h);

        var response = await h.Post("/api/v2/ai/suggest-tasks", new { roomId = keuken });

        var body = await AiHarness.Body(response);
        ShouldBeProblem(response, body, HttpStatusCode.UnprocessableEntity, "ai_invalid_response");
        Strings(body.GetProperty("errors")).Should().Contain(e => e.StartsWith("suggestions.0.name", StringComparison.Ordinal));
    }

    // ---- POST /ai/explain

    [Fact]
    public async Task Explain_addsTheConfiguredPromptToTheProviderRequest()
    {
        await using var h = await AiHarness.StartAsync(mongo);
        await SetPromptAsync(h, "planExplanation", "Leg het uit in heel eenvoudige taal.");

        await h.Post("/api/v2/ai/explain", new { planId = await h.ActivePlanId() });

        h.Request().SystemPrompt.Should().Contain("Leg het uit in heel eenvoudige taal.");
    }

    [Fact]
    public async Task Explain_returnsFourSentencesForAPlan_andStoresNothing()
    {
        await using var h = await AiHarness.StartAsync(mongo);
        var before = await h.Snapshot();

        var response = await h.Post("/api/v2/ai/explain", new { planId = await h.ActivePlanId() });

        var body = await AiHarness.Body(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await h.Snapshot()).Should().Be(before);
        var rationale = Strings(body.GetProperty("rationale"));
        rationale.Should().HaveCount(4);
        rationale[0].Should().Be("Week 1: 0 taken, verdeeld over de week.");
        var payload = AiHarness.Payload<ExplanationPayload>(h.Request());
        payload.PlanName.Should().Be("Standaard");
        h.Request().Options.Name.Should().Be("plan-explanation");
    }

    [Fact]
    public async Task Explain_sendsTheSlotsWithTaskNamesAssigneesAndDurations()
    {
        await using var h = await AiHarness.StartAsync(mongo);
        var room = await h.Room("Badkamer");
        var weekly = await h.Task("Douche", room, "1w", 30);
        var planId = await h.ActivePlanId();
        (await h.Send(HttpMethod.Put, $"/api/v2/cycle-plans/{planId}/slots", new { slots = new[] { new { taskId = weekly, weekIndex = 1, weekday = 3, assigneeId = h.P2 } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await h.Post("/api/v2/ai/explain", new { planId });

        var payload = AiHarness.Payload<ExplanationPayload>(h.Request());
        payload.Slots.Should().BeEquivalentTo([new ExplanationSlot("Douche", 1, 3, "Persoon 2", 30)]);
        payload.Users.Select(u => u.Name).Should().Equal("Persoon 1", "Persoon 2");
    }

    [Fact]
    public async Task Explain_rejectsAnExplanationWithoutExactlyFourSentences_andUnknownPlans()
    {
        var answer = JsonSerializer.Serialize(new { rationale = new[] { "een", "twee", "drie" } });
        await using var h = await AiHarness.StartAsync(mongo, new Dictionary<string, MockResponder> { ["plan-explanation"] = MockResponder.Fixed(answer) });

        var short3 = await h.Post("/api/v2/ai/explain", new { planId = await h.ActivePlanId() });
        var unknown = await h.Post("/api/v2/ai/explain", new { planId = "0123456789abcdef01234567" });
        var missing = await h.Post("/api/v2/ai/explain", new { });

        var body = await AiHarness.Body(short3);
        ShouldBeProblem(short3, body, HttpStatusCode.UnprocessableEntity, "ai_invalid_response");
        body.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
        ShouldBeProblem(unknown, await AiHarness.Body(unknown), HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(missing, await AiHarness.Body(missing), HttpStatusCode.BadRequest, "validation_error");
    }

    // ---- AI disabled, provider failures

    [Fact]
    public async Task WithTheProviderNone_suggestionsAndExplanationsAnswer503_aiDisabled()
    {
        await using var h = await AiHarness.StartAsync(mongo, realModels: true);
        var (keuken, _) = await TwoRoomsAsync(h);
        var planId = await h.ActivePlanId();

        var suggestions = await h.Post("/api/v2/ai/suggest-tasks", new { roomId = keuken });
        var explanation = await h.Post("/api/v2/ai/explain", new { planId });
        var test = await h.Post("/api/v2/ai/test", new { aiProvider = new { type = "none" } });

        ShouldBeProblem(suggestions, await AiHarness.Body(suggestions), HttpStatusCode.ServiceUnavailable, "ai_disabled");
        ShouldBeProblem(explanation, await AiHarness.Body(explanation), HttpStatusCode.ServiceUnavailable, "ai_disabled");
        ShouldBeProblem(test, await AiHarness.Body(test), HttpStatusCode.ServiceUnavailable, "ai_disabled");
    }

    [Fact]
    public async Task AModelThatHasNoAnswer_isA502_aiProviderError_withoutProviderDetails()
    {
        await using var h = await AiHarness.StartAsync(mongo, new Dictionary<string, MockResponder>());
        var (keuken, _) = await TwoRoomsAsync(h);

        var response = await h.Post("/api/v2/ai/suggest-tasks", new { roomId = keuken });

        ShouldBeProblem(response, await AiHarness.Body(response), HttpStatusCode.BadGateway, "ai_provider_error");
    }

    // ---- GET /ai/prompt-info

    [Fact]
    public async Task PromptInfo_needsNoProfile_andShowsTheBuiltInTemplatesWithTheHouseholdInstructions()
    {
        await using var h = await AiHarness.StartAsync(mongo);

        var response = await h.Send(HttpMethod.Get, "/api/v2/ai/prompt-info", noProfile: true);

        var body = await AiHarness.Body(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        var actions = body.GetProperty("actions");
        var proposal = actions.GetProperty("planProposal");
        proposal.GetProperty("system").GetString().Should().Contain("Never assign a slot to a user on a weekday listed")
            .And.Contain("{{schema}}")
            .And.Contain("Maak een praktisch vierwekenplan voor alle taken.");
        proposal.GetProperty("user").GetString().Should().Be("{{input}}");
        proposal.GetProperty("fixedPrompt").GetString().Should().Be(proposal.GetProperty("system").GetString());
        proposal.GetProperty("dynamicData").GetString().Should().StartWith("modus, alle actieve taken");
        actions.GetProperty("taskSuggestions").GetProperty("dynamicData").GetString().Should().StartWith("De gekozen ruimte");
        var defaults = body.GetProperty("defaults");
        defaults.GetProperty("planProposal").GetProperty("system").GetString().Should().NotContain("Maak een praktisch");
        defaults.GetProperty("planExplanation").GetProperty("user").GetString().Should().Be("{{input}}");
        body.GetProperty("defaults").EnumerateObject().Select(p => p.Name).Should().Equal("planProposal", "planRebalance", "taskSuggestions", "planExplanation");
    }

    [Fact]
    public async Task PromptInfo_showsAStoredTemplateInsteadOfTheBuiltInOne()
    {
        await using var h = await AiHarness.StartAsync(mongo);
        var template = new { system = "Mijn systeemprompt {{schema}}", user = "Mijn invoer {{input}}" };
        await h.Patch("/api/v2/settings", new { aiPromptTemplates = new { planProposal = template, planRebalance = template, taskSuggestions = template, planExplanation = template } });

        var body = await AiHarness.Body(await h.Send(HttpMethod.Get, "/api/v2/ai/prompt-info", noProfile: true));

        var proposal = body.GetProperty("actions").GetProperty("planProposal");
        proposal.GetProperty("system").GetString().Should().Be("Mijn systeemprompt {{schema}}");
        proposal.GetProperty("user").GetString().Should().Be("Mijn invoer {{input}}");
        proposal.GetProperty("fixedPrompt").GetString().Should().Be("Mijn systeemprompt {{schema}}");
        body.GetProperty("defaults").GetProperty("planProposal").GetProperty("system").GetString().Should().Contain("You plan household chores");
    }

    // ---- roles

    [Fact]
    public async Task EveryAssistantEndpoint_needsAPlanner_aMemberIsRefused_andNoProfileIsAProfileRequiredError()
    {
        await using var h = await AiHarness.StartAsync(mongo);
        var calls = new (string Url, object Body)[]
        {
            ("/api/v2/ai/test", new { aiProvider = new { type = "mock" } }),
            ("/api/v2/ai/propose-plan", new { }),
            ("/api/v2/ai/rebalance", new { planId = "0123456789abcdef01234567" }),
            ("/api/v2/ai/suggest-tasks", new { roomId = "0123456789abcdef01234567" }),
            ("/api/v2/ai/explain", new { planId = "0123456789abcdef01234567" }),
        };

        foreach (var (url, body) in calls)
        {
            var member = await h.Send(HttpMethod.Post, url, body, profile: h.P2);
            var anonymous = await h.Send(HttpMethod.Post, url, body, noProfile: true);

            ShouldBeProblem(member, await AiHarness.Body(member), HttpStatusCode.Forbidden, "permission_denied");
            ShouldBeProblem(anonymous, await AiHarness.Body(anonymous), HttpStatusCode.BadRequest, "profile_required");
        }

        h.Model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ABodyThatIsNotAJsonObject_isAValidationError()
    {
        await using var h = await AiHarness.StartAsync(mongo);

        var rebalance = await h.Send(HttpMethod.Post, "/api/v2/ai/rebalance", "[1]");
        var explain = await h.Send(HttpMethod.Post, "/api/v2/ai/explain", "{oops");

        ShouldBeProblem(rebalance, await AiHarness.Body(rebalance), HttpStatusCode.BadRequest, "validation_error");
        ShouldBeProblem(explain, await AiHarness.Body(explain), HttpStatusCode.BadRequest, "validation_error");
    }

    // ---- the provider is chosen from the stored settings on every call

    [Fact]
    public async Task ASettingsChange_takesEffectOnTheNextCall_withTheRealProviderChoice()
    {
        await using var h = await AiHarness.StartAsync(mongo, realModels: true);
        var room = await h.Room("Badkamer");
        await h.Task("Douche", room, "1w", 30);

        var disabled = await h.Post("/api/v2/ai/propose-plan");
        await h.Patch("/api/v2/settings", new { aiProvider = new { type = "mock" } });
        var mock = await h.Post("/api/v2/ai/propose-plan");
        await h.Patch("/api/v2/settings", new { aiProvider = new { type = "none" } });
        var disabledAgain = await h.Post("/api/v2/ai/propose-plan");
        await h.Patch("/api/v2/settings", new { aiProvider = new { type = "anthropic" } });
        var misconfigured = await h.Post("/api/v2/ai/propose-plan");

        ShouldBeProblem(disabled, await AiHarness.Body(disabled), HttpStatusCode.ServiceUnavailable, "ai_disabled");
        var mockBody = await AiHarness.Body(mock);
        mock.StatusCode.Should().Be(HttpStatusCode.OK, mockBody.ToString());
        ShouldBeProblem(disabledAgain, await AiHarness.Body(disabledAgain), HttpStatusCode.ServiceUnavailable, "ai_disabled");
        var misconfiguredBody = await AiHarness.Body(misconfigured);
        ShouldBeProblem(misconfigured, misconfiguredBody, HttpStatusCode.ServiceUnavailable, "ai_misconfigured");
        misconfiguredBody.GetProperty("detail").GetString().Should().Be("AI_API_KEY is not set");
        h.Models.Chosen.Select(c => c.Type).Should().Equal(
            Domain.Settings.AiProviderType.None,
            Domain.Settings.AiProviderType.Mock,
            Domain.Settings.AiProviderType.None,
            Domain.Settings.AiProviderType.Anthropic);
    }

    [Fact]
    public async Task TheHostWiring_usesTheStoredSettingsAndTheApiKeyFromTheEnvironment_andNeverEchoesTheKey()
    {
        const string key = "sk-test-secret-key";
        var databaseName = MongoContainerFixture.NewDatabaseName();
        using var factory = ApiFactory.ForMongo(mongo, databaseName).WithSetting("AI_API_KEY", key);
        using var client = factory.CreateClient();
        using var mongoClient = new MongoDB.Driver.MongoClient(mongo.ConnectionString);
        try
        {
            var users = await mongoClient.GetDatabase(databaseName).GetCollection<BsonDocument>("users")
                .Find(MongoDB.Driver.FilterDefinition<BsonDocument>.Empty).SortBy(u => u["createdAt"]).ToListAsync(Ct);
            var admin = users[0]["_id"].AsObjectId.ToString();

            async Task<HttpResponseMessage> Send(HttpMethod method, string url, string json)
            {
                using var request = new HttpRequestMessage(method, url) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
                request.Headers.Add("X-Profile-Id", admin);
                return await client.SendAsync(request, Ct);
            }

            // An OpenAI-compatible endpoint nothing listens on: the key is accepted (no ai_misconfigured), the call fails, nothing is echoed.
            var response = await Send(HttpMethod.Post, "/api/v2/ai/test", "{\"aiProvider\":{\"type\":\"openai-compatible\",\"endpoint\":\"http://127.0.0.1:1/v1\",\"model\":\"m\",\"timeoutSeconds\":10}}");

            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadGateway, text);
            text.Should().Contain("ai_provider_error").And.NotContain(key).And.NotContain("127.0.0.1");

            var settings = await client.GetStringAsync(new Uri("/api/v2/settings", UriKind.Relative), Ct);
            settings.Should().NotContain(key);
        }
        finally
        {
            await mongoClient.DropDatabaseAsync(databaseName, Ct);
        }
    }
}
