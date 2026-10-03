#pragma warning disable CA1861 // Literal request bodies of the scenarios.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Huishoudplanner.Adapters.Ai;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Integration.Tests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Api;

/// <summary>
/// Ports apps/server/test/ai-proposals.test.ts (propose and rebalance with validation, the one re-prompt, the completion of partial plans,
/// the editable templates and prompts) and apps/server/test/ai-draft.test.ts (the diff of an AI draft against the active plan). The model is the
/// deterministic mock behind <c>ForSelectingAModel</c>; the real HTTP pipeline and MongoDB are used. Deferred to slice 2.4: the activation of an
/// AI draft (<c>POST /cycle-plans/:id/activate</c> of ai-draft.test.ts) that clears the draft flag.
/// </summary>
public sealed class AiProposalEndpointTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Rationale = ["Week 1 uitleg.", "Week 2 uitleg.", "Week 3 uitleg.", "Week 4 uitleg."];

    private sealed record World(AiHarness H, string Weekly, string Twice, string Room);

    /// <summary>
    /// The Node <c>setup()</c>: Persoon 1 cannot do Tuesdays, a bathroom with a weekly task (30 minutes, 4 per cycle) and a two-weekly one
    /// (10 minutes, 8 per cycle). <paramref name="answer"/> is the answer of the model; it reads the ids lazily through the world.
    /// </summary>
    private async Task<World> SetupAsync(Func<MockRequest, int, World, string> answer, bool realModels = false)
    {
        World? world = null;
        var responders = new Dictionary<string, MockResponder> { ["plan-proposal"] = MockResponder.From((request, attempt) => answer(request, attempt, world!)) };
        var h = await AiHarness.StartAsync(mongo, responders, realModels);
        await h.Patch($"/api/v2/users/{h.P1}", new { unavailableWeekdays = new[] { 2 } });
        var room = await h.Room("Badkamer");
        var weekly = await h.Task("Badkamer schoonmaken", room, "1w", 30);
        var twice = await h.Task("Wastafel", room, "2w", 10);
        world = new World(h, weekly, twice, room);
        return world;
    }

    private Task<World> SetupValidAsync() => SetupAsync((request, _, _) => AiHarness.ValidAnswer(request));

    private static JsonObject PlanOf(MockRequest request) => DefaultMockResponders.DeterministicPlan(JsonDocument.Parse(request.User).RootElement);

    private static void ShouldBeProblem(HttpResponseMessage response, JsonElement body, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status, body.ToString());
        body.GetProperty("type").GetString().Should().Be($"urn:huishoudplanner:problem:{code}");
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await AiHarness.Body(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        return body;
    }

    private static async Task SetPromptAsync(AiHarness h, string use, string text)
    {
        var prompts = new Dictionary<string, string> { ["planProposal"] = "", ["planRebalance"] = "", ["taskSuggestions"] = "", ["planExplanation"] = "", [use] = text };
        await h.Patch("/api/v2/settings", new { aiPrompts = prompts });
    }

    // ---- POST /ai/propose-plan

    [Fact]
    public async Task Propose_addsTheConfiguredProposalPromptToTheProviderRequest()
    {
        var w = await SetupValidAsync();
        await using var h = w.H;
        await SetPromptAsync(h, "planProposal", "Plan zware taken nooit op opeenvolgende dagen.");

        await h.Post("/api/v2/ai/propose-plan");

        h.Request().SystemPrompt.Should().Contain("Plan zware taken nooit op opeenvolgende dagen.");
    }

    [Fact]
    public async Task Propose_withoutAnyBodyAtAll_proposesForAllActiveTasks()
    {
        var w = await SetupValidAsync();
        await using var h = w.H;

        var response = await h.PostEmpty("/api/v2/ai/propose-plan");

        await OkAsync(response);
        AiHarness.Payload<PlanPromptPayload>(h.Request()).Tasks.Should().HaveCount(2);
    }

    [Fact]
    public async Task Propose_usesTheEditableSystemAndUserTemplates_andExpandsTheirPlaceholders()
    {
        const string prefix = "Plan met deze invoer: ";
        var w = await SetupAsync((request, _, _) => AiHarness.ValidAnswer(new MockRequest(request.Name, request.System, request.User[prefix.Length..])));
        await using var h = w.H;
        var info = await AiHarness.Body(await h.Send(HttpMethod.Get, "/api/v2/ai/prompt-info", noProfile: true));
        JsonElement Default(string use) => info.GetProperty("defaults").GetProperty(use);
        var proposal = new { system = "Mijn volledige systemprompt. Antwoord volgens {{schema}}", user = prefix + "{{input}}" };
        await h.Patch("/api/v2/settings", new
        {
            aiPromptTemplates = new
            {
                planProposal = proposal,
                planRebalance = new { system = Default("planRebalance").GetProperty("system").GetString(), user = Default("planRebalance").GetProperty("user").GetString() },
                taskSuggestions = new { system = Default("taskSuggestions").GetProperty("system").GetString(), user = Default("taskSuggestions").GetProperty("user").GetString() },
                planExplanation = new { system = Default("planExplanation").GetProperty("system").GetString(), user = Default("planExplanation").GetProperty("user").GetString() },
            },
        });

        var response = await h.Post("/api/v2/ai/propose-plan");

        await OkAsync(response);
        var request = h.Request();
        request.SystemPrompt.Should().Contain("Mijn volledige systemprompt. Antwoord volgens {");
        request.SystemPrompt.Should().NotContain("{{schema}}");
        request.Messages[0].Content.Should().StartWith(prefix + "{\"mode\":\"propose\"");
    }

    [Fact]
    public async Task Propose_storesAValidProposalAsAnInactiveDraft_auditedAsAi_withTheProposalId()
    {
        var w = await SetupValidAsync();
        await using var h = w.H;
        var activeBefore = await h.ActivePlanId();
        var auditBefore = (await h.PlanAudit("create")).Count;

        var response = await h.Post("/api/v2/ai/propose-plan", new { constraints = "geen nat werk doordeweeks" });

        var body = await OkAsync(response);
        body.GetProperty("rationale").GetArrayLength().Should().Be(4);
        body.GetProperty("warnings").ValueKind.Should().Be(JsonValueKind.Array);
        var planId = body.GetProperty("planId").GetString()!;
        var proposalId = body.GetProperty("proposalId").GetString()!;
        var plan = await h.Plan(planId);
        plan["active"].AsBoolean.Should().BeFalse();
        plan["draft"].AsBoolean.Should().BeTrue();
        plan["source"].AsString.Should().Be("ai");
        plan["proposalId"].AsString.Should().Be(proposalId);
        plan["discarded"].AsBoolean.Should().BeFalse();
        plan["name"].AsString.Should().Be("AI-voorstel 2026-09-14");
        plan["slots"].AsBsonArray.Should().HaveCount(4 + 8);
        plan["rationale"].AsBsonArray.Select(v => v.AsString).Should().Equal(body.GetProperty("rationale").EnumerateArray().Select(e => e.GetString()!));
        var entries = await h.PlanAudit("create");
        entries.Should().HaveCount(auditBefore + 1);
        var entry = entries[^1];
        entry["entityId"].AsObjectId.ToString().Should().Be(planId);
        entry["source"].AsString.Should().Be("ai");
        entry["actorId"].AsObjectId.ToString().Should().Be(h.P1);
        entry["meta"]["proposalId"].AsString.Should().Be(proposalId);
        entry["meta"]["mode"].AsString.Should().Be("propose");
        entry["meta"].AsBsonDocument.Contains("basePlanId").Should().BeFalse();
        entry["after"]["draft"].AsBoolean.Should().BeTrue();
        entry["after"]["source"].AsString.Should().Be("ai");
        (await h.ActivePlanId()).Should().Be(activeBefore); // never auto-activated

        var stored = await AiHarness.Body(await h.Send(HttpMethod.Get, $"/api/v2/cycle-plans/{planId}", noProfile: true));
        stored.GetProperty("draft").GetBoolean().Should().BeTrue();
        stored.GetProperty("source").GetString().Should().Be("ai");
        stored.GetProperty("proposalId").GetString().Should().Be(proposalId);
    }

    [Fact]
    public async Task Propose_sendsTheTasks_theUsersWithAvailabilityAndBudgets_theConstraints_andASchemaNarrowedToTheirIds()
    {
        var w = await SetupValidAsync();
        await using var h = w.H;

        await h.Post("/api/v2/ai/propose-plan", new { constraints = "zaterdag maximaal een uur per persoon" });

        var request = h.Request();
        request.Options.Name.Should().Be("plan-proposal");
        request.SystemPrompt.Should().Contain("Never assign a slot to a user on a weekday listed");
        request.SystemPrompt.Should().Contain("Never use assigneeId null");
        var payload = AiHarness.Payload<PlanPromptPayload>(request);
        payload.Mode.Should().Be("propose");
        payload.Constraints.Should().Be("zaterdag maximaal een uur per persoon");
        payload.Tasks.Select(t => (t.Name, t.IntervalKey, t.PerCycle, t.DurationMinutes, t.Room)).Should().Equal(
            ("Badkamer schoonmaken", "1w", 4, 30, "Badkamer"),
            ("Wastafel", "2w", 8, 10, "Badkamer"));
        payload.Users[0].Should().BeEquivalentTo(new PromptUser(h.P1, "Persoon 1", [2], new PromptMinutes(60, 120), new PromptMinutes(60, 120)));
        payload.CurrentSlots.Should().BeNull();
        var slot = request.Options.ResponseSchema!.Value.GetProperty("properties").GetProperty("slots").GetProperty("items").GetProperty("properties");
        slot.GetProperty("taskId").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal(w.Weekly, w.Twice);
        slot.GetProperty("assigneeId").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal(h.P1, h.P2);
        request.SystemPrompt.Should().Contain(slot.GetProperty("taskId").GetProperty("enum").GetRawText());
        request.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Propose_asksForARecognizableWeekdayRhythm_asASoftPreferenceRankedBelowTheHardLimits()
    {
        var w = await SetupValidAsync();
        await using var h = w.H;

        await h.Post("/api/v2/ai/propose-plan");

        var system = h.Request().SystemPrompt;
        int IndexOfAnchor(string text)
        {
            var index = system.IndexOf(text, StringComparison.Ordinal);
            index.Should().BeGreaterThan(-1, $"the prompt contains \"{text}\"");
            return index;
        }

        var rhythm = IndexOfAnchor("recognizable rhythm");
        var line = system[(system.LastIndexOf('\n', rhythm) + 1)..system.IndexOf('\n', rhythm)];
        line.Should().StartWith("7. Soft preference, ranked below availability, the intervals and the hard daily limits").And.Contain("same weekdays");
        rhythm.Should().BeGreaterThan(IndexOfAnchor("Never assign a slot to a user on a weekday listed"));
        rhythm.Should().BeGreaterThan(IndexOfAnchor("3. Keep each individual day's minutes within maxDailyMinutes"));
        rhythm.Should().BeGreaterThan(IndexOfAnchor("6. Respect the free-text constraints"));
    }

    [Fact]
    public async Task Propose_rePromptsOnceWithTheErrorList_whenTheFirstAnswerIsNotValidJson()
    {
        var w = await SetupAsync((request, attempt, _) => attempt == 0 ? "Hier is je plan: {slots" : AiHarness.ValidAnswer(request));
        await using var h = w.H;

        var response = await h.Post("/api/v2/ai/propose-plan");

        await OkAsync(response);
        h.Model.Requests.Should().HaveCount(2);
        AiHarness.Payload<PlanPromptPayload>(h.Request(1)).PreviousErrors.Should().Equal("The answer was not valid JSON.");
        AiHarness.Payload<PlanPromptPayload>(h.Request(0)).PreviousErrors.Should().BeNull();
        (await w.H.PlanCount()).Should().Be(2);
    }

    [Fact]
    public async Task Propose_fillsEveryRequiredOccurrence_whenAModelReturnsOnlyAPartialPlan()
    {
        var w = await SetupAsync((request, _, _) =>
        {
            var payload = JsonDocument.Parse(request.User).RootElement;
            return new JsonObject
            {
                ["slots"] = new JsonArray(new JsonObject
                {
                    ["taskId"] = payload.GetProperty("tasks")[0].GetProperty("id").GetString(),
                    ["weekIndex"] = 0,
                    ["weekday"] = 1,
                    ["assigneeId"] = payload.GetProperty("users")[0].GetProperty("id").GetString(),
                }),
                ["rationale"] = new JsonArray(Rationale.Select(r => (JsonNode?)r).ToArray()),
            }.ToJsonString();
        });
        await using var h = w.H;

        var body = await OkAsync(await h.Post("/api/v2/ai/propose-plan"));

        var plan = await h.Plan(body.GetProperty("planId").GetString()!);
        var slots = plan["slots"].AsBsonArray.Select(s => s.AsBsonDocument).ToList();
        slots.Count(s => s["taskId"].AsObjectId.ToString() == w.Weekly).Should().Be(4);
        slots.Count(s => s["taskId"].AsObjectId.ToString() == w.Twice).Should().Be(8);
        body.GetProperty("warnings").EnumerateArray().Count(x => x.GetProperty("code").GetString() == "interval_mismatch").Should().Be(0);
    }

    [Fact]
    public async Task Propose_replacesSharedAssignmentsWithAConcretePersonWhoIsAvailableThatDay()
    {
        var w = await SetupAsync((request, _, _) =>
        {
            var plan = PlanOf(request);
            foreach (var slot in plan["slots"]!.AsArray())
            {
                slot!["assigneeId"] = null;
            }

            return plan.ToJsonString();
        });
        await using var h = w.H;

        var body = await OkAsync(await h.Post("/api/v2/ai/propose-plan"));

        var slots = (await h.Plan(body.GetProperty("planId").GetString()!))["slots"].AsBsonArray.Select(s => s.AsBsonDocument).ToList();
        slots.Should().OnlyContain(s => s["assigneeId"].IsObjectId);
        slots.Should().NotContain(s => s["weekday"].AsInt32 == 2 && s["assigneeId"].AsObjectId.ToString() == h.P1);
    }

    [Fact]
    public async Task Propose_rePrompts_whenTheFirstAnswerBreaksAHardRule_namingTheViolation()
    {
        World? world = null;
        world = await SetupAsync((request, attempt, _) =>
        {
            var plan = PlanOf(request);
            if (attempt == 0)
            {
                var first = plan["slots"]![0]!;
                first["weekday"] = 2;
                first["assigneeId"] = world!.H.P1;
            }

            return plan.ToJsonString();
        });
        await using var h = world.H;

        var response = await h.Post("/api/v2/ai/propose-plan");

        await OkAsync(response);
        AiHarness.Payload<PlanPromptPayload>(h.Request(1)).PreviousErrors.Should().Contain(e => e.StartsWith("assignee_unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Propose_keepsAUsableProposalAndWarns_whenAPersonExceedsTheDailyMaximumForOneDay()
    {
        var w = await SetupAsync((request, _, world) =>
        {
            var plan = PlanOf(request);
            var slots = plan["slots"]!.AsArray();
            var first = slots.First(s => s!["taskId"]!.GetValue<string>() == world.Weekly)!;
            var otherIndex = slots.ToList().FindIndex(s => s!["taskId"]!.GetValue<string>() == world.Twice);
            slots[otherIndex] = new JsonObject
            {
                ["taskId"] = world.Twice,
                ["weekIndex"] = first["weekIndex"]!.GetValue<int>(),
                ["weekday"] = first["weekday"]!.GetValue<int>(),
                ["assigneeId"] = world.H.P1,
            };
            first["assigneeId"] = world.H.P1;
            return plan.ToJsonString();
        });
        await using var h = w.H;
        await h.Patch($"/api/v2/users/{h.P1}", new { maxDailyMinutes = new { weekday = 30, weekend = 30 } });

        var body = await OkAsync(await h.Post("/api/v2/ai/propose-plan"));

        h.Model.Requests.Should().HaveCount(1);
        body.GetProperty("warnings").EnumerateArray().Select(x => x.GetProperty("code").GetString()).Should().Contain("daily_over_budget");
    }

    [Fact]
    public async Task Propose_givesUpAfterTheSecondInvalidAnswer_422WithErrors_andStoresNothing()
    {
        var w = await SetupAsync((_, _, _) => "{\"slots\":[],\"rationale\":[\"only one\"]}");
        await using var h = w.H;
        var plansBefore = await h.PlanCount();
        var auditBefore = (await h.PlanAudit("create")).Count;

        var response = await h.Post("/api/v2/ai/propose-plan");

        var body = await AiHarness.Body(response);
        ShouldBeProblem(response, body, HttpStatusCode.UnprocessableEntity, "ai_invalid_plan");
        body.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
        h.Model.Requests.Should().HaveCount(2);
        (await h.PlanCount()).Should().Be(plansBefore);
        (await h.PlanAudit("create")).Should().HaveCount(auditBefore);
    }

    [Fact]
    public async Task Propose_rejectsSlotsForTasksOutsideTheRequestedSelection()
    {
        World? world = null;
        world = await SetupAsync((request, _, _) =>
        {
            var plan = PlanOf(request);
            var slots = plan["slots"]!.AsArray();
            slots.Insert(0, new JsonObject { ["taskId"] = world!.Twice, ["weekIndex"] = 0, ["weekday"] = 3, ["assigneeId"] = null });
            return plan.ToJsonString();
        });
        await using var h = world.H;

        var response = await h.Post("/api/v2/ai/propose-plan", new { taskIds = new[] { world.Weekly } });

        var body = await AiHarness.Body(response);
        ShouldBeProblem(response, body, HttpStatusCode.UnprocessableEntity, "ai_invalid_plan");
        body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).Should().Contain(e => e.StartsWith("task_not_in_selection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Propose_planOnlyTheSelectedTasks()
    {
        var w = await SetupValidAsync();
        await using var h = w.H;

        var body = await OkAsync(await h.Post("/api/v2/ai/propose-plan", new { taskIds = new[] { w.Weekly } }));

        AiHarness.Payload<PlanPromptPayload>(h.Request()).Tasks.Select(t => t.Id).Should().Equal(w.Weekly);
        var slots = (await h.Plan(body.GetProperty("planId").GetString()!))["slots"].AsBsonArray;
        slots.Should().HaveCount(4);
    }

    [Fact]
    public async Task Propose_validatesTheBody_beforeAskingTheModel()
    {
        var w = await SetupValidAsync();
        await using var h = w.H;

        var unknown = await h.Post("/api/v2/ai/propose-plan", new { taskIds = new[] { "0123456789abcdef01234567" } });
        var empty = await h.Post("/api/v2/ai/propose-plan", new { taskIds = Array.Empty<string>() });
        var tooLong = await h.Post("/api/v2/ai/propose-plan", new { constraints = new string('x', 2001) });
        var malformed = await h.Post("/api/v2/ai/propose-plan", new { taskIds = new[] { "nope" } });
        var wrongType = await h.Post("/api/v2/ai/propose-plan", new { constraints = 5 });

        foreach (var response in new[] { unknown, empty, tooLong, malformed, wrongType })
        {
            ShouldBeProblem(response, await AiHarness.Body(response), HttpStatusCode.BadRequest, "validation_error");
        }

        (await AiHarness.Body(unknown)).GetProperty("errors").GetProperty("taskIds")[0].GetString().Should().Be("unknown_task");
        h.Model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Propose_withoutActiveTasks_isAValidationError()
    {
        await using var h = await AiHarness.StartAsync(mongo);

        var response = await h.Post("/api/v2/ai/propose-plan");

        var body = await AiHarness.Body(response);
        ShouldBeProblem(response, body, HttpStatusCode.BadRequest, "validation_error");
        body.GetProperty("errors").GetProperty("taskIds")[0].GetString().Should().Be("no_tasks");
        h.Model.Requests.Should().BeEmpty();
    }

    // ---- POST /ai/rebalance

    [Fact]
    public async Task Rebalance_sendsTheCurrentSlots_andStoresADraftNamedAfterTheBasePlan()
    {
        var w = await SetupValidAsync();
        await using var h = w.H;
        await SetPromptAsync(h, "planRebalance", "Behoud bestaande dagen als dat mogelijk is.");
        var activeId = await h.ActivePlanId();
        (await h.Send(HttpMethod.Put, $"/api/v2/cycle-plans/{activeId}/slots", new { slots = new[] { new { taskId = w.Weekly, weekIndex = 0, weekday = 1, assigneeId = h.P1 } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await h.Patch($"/api/v2/cycle-plans/{activeId}", new { weekThemes = new[] { "Keuken", "", "", "" } });

        var response = await h.Post("/api/v2/ai/rebalance", new { planId = activeId, constraints = "eerlijker verdelen" });

        var body = await OkAsync(response);
        var payload = AiHarness.Payload<PlanPromptPayload>(h.Request());
        payload.Mode.Should().Be("rebalance");
        payload.Constraints.Should().Be("eerlijker verdelen");
        h.Request().SystemPrompt.Should().Contain("Behoud bestaande dagen als dat mogelijk is.");
        payload.CurrentSlots.Should().BeEquivalentTo([new PromptSlot(w.Weekly, 0, 1, h.P1)]);
        var draftId = body.GetProperty("planId").GetString()!;
        var draft = await h.Plan(draftId);
        draft["name"].AsString.Should().Be("Standaard (herbalanceerd)");
        draft["draft"].AsBoolean.Should().BeTrue();
        draft["active"].AsBoolean.Should().BeFalse();
        draft["source"].AsString.Should().Be("ai");
        draft["weekThemes"].AsBsonArray.Select(v => v.AsString).Should().Equal("Keuken", "", "", "");
        var audit = (await h.AuditLog.Find(new BsonDocument { { "entityId", new ObjectId(draftId) }, { "action", "create" } }).ToListAsync(Ct)).Single();
        audit["meta"]["mode"].AsString.Should().Be("rebalance");
        audit["meta"]["basePlanId"].AsObjectId.ToString().Should().Be(activeId);
        audit["source"].AsString.Should().Be("ai");
        (await h.ActivePlanId()).Should().Be(activeId);
    }

    [Fact]
    public async Task Rebalance_returns404ForAnUnknownPlan_and400ForABadBody()
    {
        var w = await SetupValidAsync();
        await using var h = w.H;

        var unknown = await h.Post("/api/v2/ai/rebalance", new { planId = "0123456789abcdef01234567" });
        var missing = await h.Post("/api/v2/ai/rebalance", new { });
        var malformed = await h.Post("/api/v2/ai/rebalance", new { planId = "nope" });
        var tooLong = await h.Post("/api/v2/ai/rebalance", new { planId = await h.ActivePlanId(), constraints = new string('x', 2001) });

        ShouldBeProblem(unknown, await AiHarness.Body(unknown), HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(missing, await AiHarness.Body(missing), HttpStatusCode.BadRequest, "validation_error");
        ShouldBeProblem(malformed, await AiHarness.Body(malformed), HttpStatusCode.BadRequest, "validation_error");
        ShouldBeProblem(tooLong, await AiHarness.Body(tooLong), HttpStatusCode.BadRequest, "validation_error");
        h.Model.Requests.Should().BeEmpty();
    }

    // ---- AI disabled

    [Fact]
    public async Task WithTheProviderNone_aProposalIs503_aiDisabled_andStoresNothing()
    {
        var w = await SetupAsync((_, _, _) => "{}", realModels: true);
        await using var h = w.H;
        var plansBefore = await h.PlanCount();

        var response = await h.Post("/api/v2/ai/propose-plan");

        ShouldBeProblem(response, await AiHarness.Body(response), HttpStatusCode.ServiceUnavailable, "ai_disabled");
        (await h.PlanCount()).Should().Be(plansBefore);
    }

    [Fact]
    public async Task WithAMissingKey_aProposalIs503_aiMisconfigured_andStoresNothing()
    {
        var w = await SetupAsync((_, _, _) => "{}", realModels: true);
        await using var h = w.H;
        await h.Patch("/api/v2/settings", new { aiProvider = new { type = "anthropic" } });
        var plansBefore = await h.PlanCount();

        var response = await h.Post("/api/v2/ai/propose-plan");

        ShouldBeProblem(response, await AiHarness.Body(response), HttpStatusCode.ServiceUnavailable, "ai_misconfigured");
        (await h.PlanCount()).Should().Be(plansBefore);
    }

    [Fact]
    public async Task AFailingProvider_isA502_andStoresNothing()
    {
        // No responder for the name: the mock answers like an unreachable provider.
        await using var h = await AiHarness.StartAsync(mongo, new Dictionary<string, MockResponder>());
        await h.Task("Douche", await h.Room("Badkamer"), "1w", 30);
        var plansBefore = await h.PlanCount();

        var response = await h.Post("/api/v2/ai/propose-plan");

        ShouldBeProblem(response, await AiHarness.Body(response), HttpStatusCode.BadGateway, "ai_provider_error");
        (await h.PlanCount()).Should().Be(plansBefore);
    }

    // ---- the diff of an AI draft (ai-draft.test.ts)

    /// <summary>
    /// Active plan:  weekly  w0 Mon P1, w1 Mon P1 · twice w0 Wed P2, w2 Sat P2.
    /// AI proposal:  weekly  w0 Mon P1, w1 Tue P2 · twice w0 Wed P1           · ramen w2 Fri P1 (the model left it without a person).
    /// </summary>
    private async Task<(World World, string Ramen, string ActiveId, string DraftId)> DraftSetupAsync()
    {
        string ramen = string.Empty;
        var world = await SetupAsync((_, _, w) => new JsonObject
        {
            ["slots"] = new JsonArray(
                Slot(w.Weekly, 0, 1, w.H.P1),
                Slot(w.Weekly, 1, 2, w.H.P2),
                Slot(w.Twice, 0, 3, w.H.P1),
                Slot(ramen, 2, 5, null)),
            ["rationale"] = new JsonArray("Week 1.", "Week 2.", "Week 3.", "Week 4."),
        }.ToJsonString());
        var h = world.H;
        // This suite tests proposal diffs, not cycle completion. Optional (quarterly) tasks keep the sparse layout unchanged:
        // the two tasks of the setup are replaced by quarterly ones.
        await h.Patch($"/api/v2/tasks/{world.Weekly}", new { intervalKey = "quarter" });
        await h.Patch($"/api/v2/tasks/{world.Twice}", new { intervalKey = "quarter" });
        await h.Patch($"/api/v2/users/{h.P1}", new { unavailableWeekdays = Array.Empty<int>() });
        ramen = await h.Task("Ramen lappen", world.Room, "quarter", 60);
        var activeId = await h.ActivePlanId();
        var put = await h.Send(HttpMethod.Put, $"/api/v2/cycle-plans/{activeId}/slots", new
        {
            slots = new[]
            {
                new { taskId = world.Weekly, weekIndex = 0, weekday = 1, assigneeId = h.P1 },
                new { taskId = world.Weekly, weekIndex = 1, weekday = 1, assigneeId = h.P1 },
                new { taskId = world.Twice, weekIndex = 0, weekday = 3, assigneeId = h.P2 },
                new { taskId = world.Twice, weekIndex = 2, weekday = 6, assigneeId = h.P2 },
            },
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));
        var proposal = await OkAsync(await h.Post("/api/v2/ai/propose-plan"));
        return (world, ramen, activeId, proposal.GetProperty("planId").GetString()!);

        static JsonObject Slot(string task, int week, int weekday, string? assignee) =>
            new() { ["taskId"] = task, ["weekIndex"] = week, ["weekday"] = weekday, ["assigneeId"] = assignee };
    }

    [Fact]
    public async Task Diff_ofAnAiDraft_listsAddedRemovedAndMovedSlotsAgainstTheActivePlan()
    {
        var (world, ramen, activeId, draftId) = await DraftSetupAsync();
        await using var h = world.H;

        var response = await h.Send(HttpMethod.Get, $"/api/v2/cycle-plans/{draftId}/diff?against=active", noProfile: true);

        var diff = await OkAsync(response);
        diff.GetProperty("planId").GetString().Should().Be(draftId);
        diff.GetProperty("againstPlanId").GetString().Should().Be(activeId);
        diff.GetProperty("unchanged").GetInt32().Should().Be(1);
        var added = diff.GetProperty("added").EnumerateArray().ToList();
        added.Should().ContainSingle();
        added[0].GetRawText().Should().Be(
            $"{{\"taskId\":\"{ramen}\",\"taskName\":\"Ramen lappen\",\"roomName\":\"Badkamer\",\"durationMinutes\":60,\"weekIndex\":2,\"weekday\":5,\"assigneeId\":\"{h.P1}\"}}");
        var removed = diff.GetProperty("removed").EnumerateArray().ToList();
        removed.Should().ContainSingle();
        removed[0].GetRawText().Should().Be(
            $"{{\"taskId\":\"{world.Twice}\",\"taskName\":\"Wastafel\",\"roomName\":\"Badkamer\",\"durationMinutes\":10,\"weekIndex\":2,\"weekday\":6,\"assigneeId\":\"{h.P2}\"}}");
        var moved = diff.GetProperty("moved").EnumerateArray().ToList();
        moved.Should().HaveCount(2);
        moved.Select(m => m.GetRawText()).Should().BeEquivalentTo(
        [
            $"{{\"taskId\":\"{world.Weekly}\",\"taskName\":\"Badkamer schoonmaken\",\"roomName\":\"Badkamer\",\"durationMinutes\":30,\"from\":{{\"weekIndex\":1,\"weekday\":1,\"assigneeId\":\"{h.P1}\"}},\"to\":{{\"weekIndex\":1,\"weekday\":2,\"assigneeId\":\"{h.P2}\"}}}}",
            $"{{\"taskId\":\"{world.Twice}\",\"taskName\":\"Wastafel\",\"roomName\":\"Badkamer\",\"durationMinutes\":10,\"from\":{{\"weekIndex\":0,\"weekday\":3,\"assigneeId\":\"{h.P2}\"}},\"to\":{{\"weekIndex\":0,\"weekday\":3,\"assigneeId\":\"{h.P1}\"}}}}",
        ]);
    }

    [Fact]
    public async Task Diff_ofAnAiDraft_includesTheMinutesPerPersonPerWeekBeforeAndAfter()
    {
        var (world, _, _, draftId) = await DraftSetupAsync();
        await using var h = world.H;

        var diff = await OkAsync(await h.Send(HttpMethod.Get, $"/api/v2/cycle-plans/{draftId}/diff", noProfile: true));

        int[] Minutes(string side, string user) =>
            [.. diff.GetProperty("summary").GetProperty(side).EnumerateArray()
                .Select(week => week.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("userId").GetString() == user).GetProperty("minutes").GetInt32())];
        Minutes("before", h.P1).Should().Equal(30, 30, 0, 0);
        Minutes("before", h.P2).Should().Equal(10, 0, 10, 0);
        Minutes("after", h.P1).Should().Equal(40, 0, 60, 0);
        Minutes("after", h.P2).Should().Equal(0, 30, 0, 0);
    }

    [Fact]
    public async Task Diff_returns404ForAnUnknownPlan_and400ForAnUnsupportedComparison()
    {
        var (world, _, _, draftId) = await DraftSetupAsync();
        await using var h = world.H;

        var unknown = await h.Send(HttpMethod.Get, "/api/v2/cycle-plans/0123456789abcdef01234567/diff", noProfile: true);
        var yesterday = await h.Send(HttpMethod.Get, $"/api/v2/cycle-plans/{draftId}/diff?against=yesterday", noProfile: true);

        ShouldBeProblem(unknown, await AiHarness.Body(unknown), HttpStatusCode.NotFound, "not_found");
        ShouldBeProblem(yesterday, await AiHarness.Body(yesterday), HttpStatusCode.BadRequest, "validation_error");
    }
}
