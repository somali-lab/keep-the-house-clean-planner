#pragma warning disable CA1861 // Literal request bodies of the scenarios.

using System.Text.Json;
using Huishoudplanner.Application.Tests.CyclePlans;
using Huishoudplanner.Application.Tests.Settings;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Application.Tests.Ai;

/// <summary>
/// The AI use cases (ai-assist, ai-proposals and ai-draft of the Node server) over in-memory ports and a scripted model: every result variant,
/// the audit input, the one re-prompt, and "nothing is stored when the proposal fails". The HTTP behaviour and the stored documents are
/// covered by the integration tests.
/// </summary>
public sealed class AiServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Actor Planner = CyclePlanWorld.Planner;

    private static readonly string ShortPlan = "{\"slots\":[],\"rationale\":[\"only one\"]}";

    private static OneOf<ChatReply, AiUnavailable, PortError> Disabled => new AiUnavailable(AiUnavailableReason.Disabled, "The AI assistant is disabled");

    // ---- propose

    [Fact]
    public async Task Propose_stores_a_valid_answer_as_an_inactive_draft_and_audits_it_as_ai()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));

        var result = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(null, "geen nat werk"), Ct);

        var proposal = result.AsT0;
        proposal.Plan.Should().Match<CyclePlan>(p => !p.Active && p.Draft && p.Source == PlanSources.Ai && !p.Discarded && p.ProposalId == proposal.ProposalId);
        proposal.Plan.Name.Should().Be("AI-voorstel 2026-10-03");
        proposal.Plan.Slots.Count(s => s.TaskId == world.Weekly.Id).Should().Be(4);
        proposal.Plan.Slots.Count(s => s.TaskId == world.Twice.Id).Should().Be(8);
        proposal.Plan.Rationale.Should().Equal(proposal.Rationale).And.HaveCount(4);
        proposal.Plan.WeekThemes.Should().Equal(CyclePlanRules.EmptyWeekThemes);
        Guid.TryParse(proposal.ProposalId, out _).Should().BeTrue();
        world.Plans.Plans.Items.Should().Contain(p => p.Id == proposal.Plan.Id);
        world.Plans.Plans.Items.Count(p => p.Active).Should().Be(1);

        var entry = world.Plans.Audit.Entries.Should().ContainSingle().Subject;
        entry.Actor.Should().Be(new AuditActor(Planner.ActorId, AuditSource.Ai));
        entry.Entity.Should().Be(AuditEntity.CyclePlan);
        entry.EntityId.Should().Be(proposal.Plan.Id);
        entry.Action.Should().Be(AuditAction.Create);
        entry.Before.Count.Should().Be(0);
        entry.After.Keys.Should().Contain(["name", "active", "slots", "weekThemes", "draft", "source", "proposalId", "rationale", "discarded"]);
        entry.Meta!.Keys.Should().Equal("proposalId", "mode");
        entry.Meta["proposalId"].Should().Be(AuditValue.FromString(proposal.ProposalId));
        entry.Meta["mode"].Should().Be(AuditValue.FromString("propose"));
        world.Plans.Plans.Writes.Should().Be(1);
    }

    [Fact]
    public async Task Propose_asks_the_model_once_with_the_active_tasks_and_people_and_a_schema_narrowed_to_their_ids()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));
        world.Plans.Task("Oude taak", world.Badkamer.Id, "1w", 5, active: false);
        world.AddPerson("Weg", active: false);
        world.AddPerson("Beperkt", unavailable: [2]);

        await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        world.Model.Requests.Should().ContainSingle();
        var request = world.Model.Requests[0];
        request.Options.Name.Should().Be("plan-proposal");
        var payload = world.Model.PlanPayload();
        payload.Mode.Should().Be("propose");
        payload.Tasks.Select(t => t.Name).Should().Equal("Badkamer schoonmaken", "Wastafel");
        payload.Tasks[0].Should().BeEquivalentTo(new PromptTask(world.Weekly.Id, "Badkamer schoonmaken", "Badkamer", "1w", "1x per week", 4, 7, 30));
        payload.Users.Select(u => u.Name).Should().Equal("Persoon 1", "Persoon 2", "Beperkt");
        payload.Users[2].UnavailableWeekdays.Should().Equal(2);
        payload.CurrentSlots.Should().BeNull();
        payload.Constraints.Should().BeNull();
        var taskEnum = request.Options.ResponseSchema!.Value.GetProperty("properties").GetProperty("slots").GetProperty("items").GetProperty("properties").GetProperty("taskId").GetProperty("enum");
        taskEnum.EnumerateArray().Select(e => e.GetString()).Should().Equal(world.Weekly.Id, world.Twice.Id);
        world.Models.Chosen.Should().ContainSingle();
    }

    [Fact]
    public async Task Propose_re_prompts_once_with_the_errors_and_stores_the_second_answer()
    {
        var world = AiWorld.Replying((request, attempt) => attempt == 0 ? "geen json" : AiWorld.ValidPlan(request));

        var result = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        result.IsT0.Should().BeTrue();
        world.Model.Requests.Should().HaveCount(2);
        world.Model.PlanPayload(0).PreviousErrors.Should().BeNull();
        world.Model.PlanPayload(1).PreviousErrors.Should().Equal("The answer was not valid JSON.");
    }

    [Fact]
    public async Task Propose_feeds_a_broken_hard_rule_back_by_name()
    {
        var world = AiWorld.Replying((request, attempt) =>
        {
            var payload = JsonSerializer.Deserialize<PlanPromptPayload>(request.Messages[^1].Content, PromptJson.Options)!;
            var first = payload.Tasks[0];
            var user = payload.Users[0];
            var slot = attempt == 0
                ? new { taskId = first.Id, weekIndex = 0, weekday = 2, assigneeId = user.Id }
                : new { taskId = first.Id, weekIndex = 0, weekday = 1, assigneeId = user.Id };
            return JsonSerializer.Serialize(new { slots = new[] { slot }, rationale = new[] { "a", "b", "c", "d" } });
        });
        var index = world.Plans.People.Users.FindIndex(u => u.Id == world.P1.Id);
        world.Plans.People.Users[index] = world.P1 with { UnavailableWeekdays = [2] };

        var result = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        result.IsT0.Should().BeTrue();
        world.Model.PlanPayload(1).PreviousErrors.Should().Contain(e => e.StartsWith("assignee_unavailable (slot 0, task " + world.Weekly.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Propose_gives_up_after_two_invalid_answers_and_stores_nothing()
    {
        var world = AiWorld.Replying((_, _) => ShortPlan);

        var result = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        var invalid = result.AsT6;
        invalid.Errors.Should().NotBeEmpty();
        world.Model.Requests.Should().HaveCount(2);
        world.Plans.Plans.Writes.Should().Be(0);
        world.Plans.Audit.Entries.Should().BeEmpty();
        world.Plans.Plans.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Propose_rejects_slots_for_tasks_outside_the_selection_and_names_them()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));
        // A slot for the second task although only the first was selected.
        var other = world.Twice.Id;
        world.Models.Model = ScriptedModel.Replying((request, _) =>
        {
            var plan = JsonDocument.Parse(AiWorld.ValidPlan(request)).RootElement;
            var slots = plan.GetProperty("slots").EnumerateArray().Select(s => s.GetRawText()).Prepend($"{{\"taskId\":\"{other}\",\"weekIndex\":0,\"weekday\":3,\"assigneeId\":null}}");
            return $"{{\"slots\":[{string.Join(",", slots)}],\"rationale\":{AiWorld.Rationale}}}";
        });

        var result = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand([world.Weekly.Id]), Ct);

        result.AsT6.Errors.Should().Contain(e => e.StartsWith("task_not_in_selection (slot 0, task " + other, StringComparison.Ordinal));
        world.Plans.Plans.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Propose_with_a_selection_plans_only_those_tasks()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));

        var result = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand([world.Weekly.Id.ToUpperInvariant()]), Ct);

        result.AsT0.Plan.Slots.Should().OnlyContain(s => s.TaskId == world.Weekly.Id);
        world.Model.PlanPayload().Tasks.Select(t => t.Id).Should().Equal(world.Weekly.Id);
    }

    [Fact]
    public async Task Propose_answers_a_validation_error_for_unknown_inactive_or_no_tasks_without_asking_the_model()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));
        var inactive = world.Plans.Task("Uit", world.Badkamer.Id, "1w", 5, active: false);

        var unknown = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(["0123456789abcdef01234567"]), Ct);
        var notActive = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand([inactive.Id]), Ct);
        world.Plans.Tasks.Items.Clear();
        var none = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        unknown.AsT2.Errors["taskIds"].Should().Equal("unknown_task");
        notActive.AsT2.Errors["taskIds"].Should().Equal("unknown_task");
        none.AsT2.Errors["taskIds"].Should().Equal("no_tasks");
        world.Model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Propose_validates_the_command_before_reading_anything()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));

        var empty = (await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand([]), Ct)).AsT2;
        var malformed = (await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(["nope"]), Ct)).AsT2;
        var tooLong = (await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(null, new string('x', 2001)), Ct)).AsT2;
        var padded = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(null, "  " + new string('x', 2000) + "  "), Ct);

        empty.Errors["taskIds"].Should().Equal("out_of_range");
        malformed.Errors.Keys.Should().Equal("taskIds[0]");
        tooLong.Errors["constraints"].Should().Equal("out_of_range");
        padded.IsT0.Should().BeTrue();
        world.Model.PlanPayload().Constraints.Should().HaveLength(2000);
    }

    [Fact]
    public async Task Propose_leaves_out_constraints_that_are_blank()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));

        await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(null, "   "), Ct);

        world.Model.PlanPayload().Constraints.Should().BeNull();
    }

    [Fact]
    public async Task Propose_passes_a_disabled_or_misconfigured_provider_on_without_a_re_prompt_or_a_write()
    {
        foreach (var reason in new[] { AiUnavailableReason.Disabled, AiUnavailableReason.Misconfigured })
        {
            var world = new AiWorld(ScriptedModel.Failing(new AiUnavailable(reason, "detail")));

            var result = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

            result.AsT4.Should().Be(new AiUnavailable(reason, "detail"));
            world.Model.Requests.Should().ContainSingle();
            world.Plans.Plans.Writes.Should().Be(0);
        }
    }

    [Fact]
    public async Task Propose_turns_a_provider_failure_into_a_failure_value_on_the_first_or_the_second_call()
    {
        var first = new AiWorld(ScriptedModel.Failing(new PortError("Ollama could not be reached")));
        var second = new AiWorld(new ScriptedModel((_, attempt) => attempt == 0 ? new ChatReply("geen json") : new PortError("Ollama timed out after 60 seconds")));

        var firstResult = await first.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);
        var secondResult = await second.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        firstResult.AsT5.Should().Be(new AiProviderFailure("Ollama could not be reached"));
        secondResult.AsT5.Should().Be(new AiProviderFailure("Ollama timed out after 60 seconds"));
        first.Model.Requests.Should().ContainSingle();
        second.Model.Requests.Should().HaveCount(2);
        first.Plans.Plans.Writes.Should().Be(0);
        second.Plans.Plans.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Propose_answers_settings_missing_before_asking_the_model()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));
        world.Plans.Settings.Document = null;

        var result = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        result.IsT3.Should().BeTrue();
        world.Model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Propose_rolls_the_draft_back_when_the_audit_entry_cannot_be_written()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));
        world.Plans.Audit.Failure = new PortError("audit down");

        var result = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        result.AsT8.Message.Should().Be("audit down");
        world.Plans.Plans.Items.Should().ContainSingle();
        world.Plans.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task Propose_passes_on_a_transaction_that_keeps_conflicting()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));
        world.Plans.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "busy");

        var result = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        result.AsT7.Code.Should().Be("write_conflict");
    }

    [Fact]
    public async Task Propose_writes_nothing_before_the_model_has_answered()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));
        var writesAtAsk = -1;
        world.Models.Model = new ScriptedModel((request, _) =>
        {
            writesAtAsk = world.Plans.Plans.Writes;
            return new ChatReply(AiWorld.ValidPlan(request));
        });

        await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        writesAtAsk.Should().Be(0);
        world.Plans.Plans.Writes.Should().Be(1);
    }

    [Fact]
    public async Task Propose_reports_a_failing_store_as_a_port_error()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));
        world.Plans.Tasks.Items.Clear();
        world.Plans.Task("Een", world.Badkamer.Id);
        world.Plans.RoomStore.Failure = new PortError("rooms down");

        var result = await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        result.AsT8.Message.Should().Be("rooms down");
        world.Model.Requests.Should().BeEmpty();
    }

    // ---- which instructions are added to the prompt

    [Fact]
    public async Task The_default_household_instructions_are_added_when_the_configured_text_is_empty_and_the_configured_text_wins_when_it_is_not()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));
        world.Plans.Settings.Document = world.Plans.Settings.Document! with { AiPrompts = new AiPrompts("   ", "", "", "") };
        await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);
        world.Plans.Settings.Document = world.Plans.Settings.Document with { AiPrompts = new AiPrompts("  Eigen wens  ", "", "", "") };
        await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        world.Model.Requests[0].SystemPrompt.Should().EndWith(SettingsDefaults.AiPrompts.PlanProposal);
        world.Model.Requests[1].SystemPrompt.Should().EndWith("\nEigen wens").And.NotContain(SettingsDefaults.AiPrompts.PlanProposal);
    }

    [Fact]
    public async Task A_stored_template_replaces_the_built_in_prompt_and_no_household_instructions_are_added()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlanFromUser(request.Messages[0].Content["Plan: ".Length..]));
        var template = new AiPromptTemplate("Mijn prompt {{schema}}", "Plan: {{input}}");
        world.Plans.Settings.Document = world.Plans.Settings.Document! with
        {
            AiPromptTemplates = new AiPromptTemplates(template, template, template, template),
            AiPrompts = new AiPrompts("negeer", "negeer", "negeer", "negeer"),
        };

        await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        var request = world.Model.Requests[0];
        request.SystemPrompt.Should().StartWith("Mijn prompt {\"type\":\"object\"").And.NotContain("negeer");
        request.Messages[0].Content.Should().StartWith("Plan: {\"mode\":\"propose\"");
    }

    [Fact]
    public async Task The_model_is_chosen_from_the_stored_provider_settings_on_every_call()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));

        await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);
        world.Plans.Settings.Document = world.Plans.Settings.Document! with { AiProvider = new AiProviderSettings(AiProviderType.Ollama, "http://x:11434", "qwen3:8b", 90) };
        await world.Service.ProposePlanAsync(Planner, new ProposePlanCommand(), Ct);

        world.Models.Chosen.Should().Equal(
            new AiProviderSettings(AiProviderType.None),
            new AiProviderSettings(AiProviderType.Ollama, "http://x:11434", "qwen3:8b", 90));
    }

    // ---- rebalance

    [Fact]
    public async Task Rebalance_sends_the_current_slots_and_stores_a_draft_named_after_the_base_plan_with_its_week_themes()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));
        var basePlan = world.Plans.Plan("Zomer", active: false, daysAgo: 2, new CyclePlanSlot(world.Weekly.Id, 0, 1, world.P1.Id));
        world.Plans.Plans.Items[^1] = basePlan with { WeekThemes = ["Keuken", "", "", ""] };

        var result = await world.Service.RebalancePlanAsync(Planner, new RebalancePlanCommand(basePlan.Id.ToUpperInvariant(), "eerlijker"), Ct);

        var proposal = result.AsT0;
        proposal.Plan.Name.Should().Be("Zomer (herbalanceerd)");
        proposal.Plan.WeekThemes.Should().Equal("Keuken", "", "", "");
        proposal.Plan.Draft.Should().BeTrue();
        var payload = world.Model.PlanPayload();
        payload.Mode.Should().Be("rebalance");
        payload.Constraints.Should().Be("eerlijker");
        payload.CurrentSlots.Should().BeEquivalentTo([new PromptSlot(world.Weekly.Id, 0, 1, world.P1.Id)]);
        var meta = world.Plans.Audit.Entries.Single().Meta!;
        meta["mode"].Should().Be(AuditValue.FromString("rebalance"));
        meta["basePlanId"].Should().Be(new AuditObjectId(basePlan.Id));
        world.Plans.Audit.Entries.Single().Actor.Source.Should().Be(AuditSource.Ai);
    }

    [Fact]
    public async Task Rebalance_uses_the_rebalance_prompt_and_template()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));
        world.Plans.Settings.Document = world.Plans.Settings.Document! with { AiPrompts = new AiPrompts("Voorstel", "Herverdeel zo", "", "") };

        await world.Service.RebalancePlanAsync(Planner, new RebalancePlanCommand(world.Standaard.Id), Ct);

        world.Model.Requests[0].SystemPrompt.Should().EndWith("Herverdeel zo");
    }

    [Fact]
    public async Task Rebalance_answers_not_found_for_an_unknown_plan_and_validation_errors_for_a_bad_command()
    {
        var world = AiWorld.Replying((request, _) => AiWorld.ValidPlan(request));

        var unknown = await world.Service.RebalancePlanAsync(Planner, new RebalancePlanCommand("0123456789abcdef01234567"), Ct);
        var malformed = await world.Service.RebalancePlanAsync(Planner, new RebalancePlanCommand("nope"), Ct);
        var tooLong = await world.Service.RebalancePlanAsync(Planner, new RebalancePlanCommand(world.Standaard.Id, new string('x', 2001)), Ct);

        unknown.IsT1.Should().BeTrue();
        malformed.AsT2.Errors.Keys.Should().Equal("planId");
        tooLong.AsT2.Errors.Keys.Should().Equal("constraints");
        world.Model.Requests.Should().BeEmpty();
    }

    // ---- suggest tasks

    private static string Suggestions(params string[] names) =>
        "{\"suggestions\":[" + string.Join(",", names.Select(n => $"{{\"name\":\"{n}\",\"intervalKey\":\"4wk\",\"durationMinutes\":15,\"notes\":\" n \"}}")) + "]}";

    [Fact]
    public async Task Suggest_sends_the_room_its_tasks_the_other_active_tasks_and_the_intervals_and_stores_nothing()
    {
        var world = AiWorld.Replying((_, _) => Suggestions("Plinten"));
        var inKeuken = world.Plans.Task("Aanrecht", world.Keuken.Id, "1w", 20);
        world.Plans.Task("Oud", world.Keuken.Id, "1w", 5, active: false);
        world.Plans.Task("Uit", world.Badkamer.Id, "1w", 5, active: false);

        var result = await world.Service.SuggestTasksAsync(world.Keuken.Id, Ct);

        result.AsT0.Should().Equal(new TaskSuggestion("Plinten", "4wk", 15, "n"));
        var payload = world.Model.Payload<TaskSuggestionPayload>();
        payload.Room.Should().Be("Keuken");
        payload.ExistingTasks.Select(t => t.Name).Should().Equal("Aanrecht", "Oud");
        payload.OtherTasks.Should().BeEquivalentTo([new OtherTaskInfo("Badkamer", "Badkamer schoonmaken"), new OtherTaskInfo("Badkamer", "Wastafel")]);
        payload.Intervals.Select(i => i.Key).Should().Equal("daily", "3w", "2w", "1w", "2wk", "4wk", "quarter");
        world.Model.Requests[0].Options.Name.Should().Be("task-suggestions");
        world.Plans.Plans.Writes.Should().Be(0);
        world.Plans.Audit.Entries.Should().BeEmpty();
        inKeuken.Should().NotBeNull();
    }

    [Fact]
    public async Task Suggest_drops_names_that_exist_in_the_room_including_inactive_tasks_and_earlier_suggestions()
    {
        var world = AiWorld.Replying((_, _) => Suggestions("Aanrecht", "OUD", "Plinten", "plinten"));
        world.Plans.Task("Aanrecht", world.Keuken.Id, "1w", 20);
        world.Plans.Task("Oud", world.Keuken.Id, "1w", 5, active: false);

        var result = await world.Service.SuggestTasksAsync(world.Keuken.Id, Ct);

        result.AsT0.Select(s => s.Name).Should().Equal("Plinten");
    }

    [Fact]
    public async Task Suggest_answers_not_found_for_an_unknown_room_and_a_validation_error_for_a_malformed_id()
    {
        var world = AiWorld.Replying((_, _) => Suggestions("Plinten"));

        var unknown = await world.Service.SuggestTasksAsync("0123456789abcdef01234567", Ct);
        var malformed = await world.Service.SuggestTasksAsync("nope", Ct);

        unknown.IsT1.Should().BeTrue();
        malformed.AsT2.Errors.Keys.Should().Equal("roomId");
        world.Model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Suggest_reports_an_unusable_answer_and_does_not_re_prompt()
    {
        var world = AiWorld.Replying((_, _) => "geen json");

        var result = await world.Service.SuggestTasksAsync(world.Keuken.Id, Ct);

        result.AsT6.Message.Should().Be("The AI answer was not valid JSON");
        world.Model.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Suggest_reports_a_provider_failure_a_disabled_provider_and_missing_settings()
    {
        var failing = new AiWorld(ScriptedModel.Failing(new PortError("Anthropic API returned HTTP 401")));
        var disabled = new AiWorld(ScriptedModel.Failing(Disabled));
        var missing = AiWorld.Replying((_, _) => Suggestions("x"));
        missing.Plans.Settings.Document = null;

        (await failing.Service.SuggestTasksAsync(failing.Keuken.Id, Ct)).AsT5.Message.Should().Be("Anthropic API returned HTTP 401");
        (await disabled.Service.SuggestTasksAsync(disabled.Keuken.Id, Ct)).AsT4.Reason.Should().Be(AiUnavailableReason.Disabled);
        (await missing.Service.SuggestTasksAsync(missing.Keuken.Id, Ct)).IsT3.Should().BeTrue();
    }

    [Fact]
    public async Task Suggest_adds_the_task_suggestion_instructions_of_the_household()
    {
        var world = AiWorld.Replying((_, _) => Suggestions("Plinten"));
        world.Plans.Settings.Document = world.Plans.Settings.Document! with { AiPrompts = new AiPrompts("", "", "Noem seizoenstaken.", "") };

        await world.Service.SuggestTasksAsync(world.Keuken.Id, Ct);

        world.Model.Requests[0].SystemPrompt.Should().EndWith("Noem seizoenstaken.");
    }

    // ---- explain

    [Fact]
    public async Task Explain_sends_the_slots_with_names_and_durations_and_returns_four_sentences_without_storing()
    {
        var world = AiWorld.Replying((_, _) => "{\"rationale\":[\"1\",\"2\",\"3\",\"4\"]}");
        var plan = world.Plans.Plan("Zomer", false, 1, new CyclePlanSlot(world.Weekly.Id, 1, 3, world.P2.Id), new CyclePlanSlot("0123456789abcdef01234567", 0, 0, null));

        var result = await world.Service.ExplainPlanAsync(plan.Id, Ct);

        result.AsT0.Should().Equal("1", "2", "3", "4");
        var payload = world.Model.Payload<ExplanationPayload>();
        payload.PlanName.Should().Be("Zomer");
        payload.Users.Select(u => u.Name).Should().Equal("Persoon 1", "Persoon 2");
        payload.Slots.Should().BeEquivalentTo(
        [
            new ExplanationSlot("Badkamer schoonmaken", 1, 3, "Persoon 2", 30),
            new ExplanationSlot("0123456789abcdef01234567", 0, 0, null, 0),
        ]);
        world.Model.Requests[0].Options.Name.Should().Be("plan-explanation");
        world.Plans.Audit.Entries.Should().BeEmpty();
        world.Plans.Plans.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Explain_names_an_inactive_person_of_a_slot_but_does_not_list_them_among_the_users()
    {
        var world = AiWorld.Replying((_, _) => "{\"rationale\":[\"1\",\"2\",\"3\",\"4\"]}");
        var gone = world.AddPerson("Weg", active: false);
        var plan = world.Plans.Plan("Zomer", false, 1, new CyclePlanSlot(world.Weekly.Id, 0, 1, gone.Id));

        await world.Service.ExplainPlanAsync(plan.Id, Ct);

        var payload = world.Model.Payload<ExplanationPayload>();
        payload.Slots.Single().Assignee.Should().Be("Weg");
        payload.Users.Select(u => u.Name).Should().NotContain("Weg");
    }

    [Fact]
    public async Task Explain_rejects_an_answer_without_exactly_four_sentences_and_unknown_plans()
    {
        var world = AiWorld.Replying((_, _) => "{\"rationale\":[\"een\",\"twee\",\"drie\"]}");

        var short3 = await world.Service.ExplainPlanAsync(world.Standaard.Id, Ct);
        var unknown = await world.Service.ExplainPlanAsync("0123456789abcdef01234567", Ct);
        var malformed = await world.Service.ExplainPlanAsync("nope", Ct);

        short3.AsT6.Errors.Should().NotBeEmpty();
        unknown.IsT1.Should().BeTrue();
        malformed.AsT2.Errors.Keys.Should().Equal("planId");
        world.Model.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Explain_reports_a_provider_failure_and_a_disabled_provider()
    {
        var failing = new AiWorld(ScriptedModel.Failing(new PortError("timed out")));
        var disabled = new AiWorld(ScriptedModel.Failing(Disabled));

        (await failing.Service.ExplainPlanAsync(failing.Standaard.Id, Ct)).AsT5.Message.Should().Be("timed out");
        (await disabled.Service.ExplainPlanAsync(disabled.Standaard.Id, Ct)).AsT4.Reason.Should().Be(AiUnavailableReason.Disabled);
    }

    [Fact]
    public async Task Explain_adds_the_explanation_instructions_of_the_household()
    {
        var world = AiWorld.Replying((_, _) => "{\"rationale\":[\"1\",\"2\",\"3\",\"4\"]}");
        world.Plans.Settings.Document = world.Plans.Settings.Document! with { AiPrompts = new AiPrompts("", "", "", "Heel eenvoudig.") };

        await world.Service.ExplainPlanAsync(world.Standaard.Id, Ct);

        world.Model.Requests[0].SystemPrompt.Should().EndWith("Heel eenvoudig.");
    }

    // ---- connection test

    [Fact]
    public async Task The_connection_test_asks_for_ok_true_and_stores_nothing()
    {
        var world = AiWorld.Replying((_, _) => "```json\n{\"ok\":true}\n```");

        var result = await world.Service.TestConnectionAsync(new AiProviderSettings(AiProviderType.Mock), Ct);

        result.IsT0.Should().BeTrue();
        var request = world.Model.Requests.Should().ContainSingle().Subject;
        request.Options.Name.Should().Be("connection-test");
        request.SystemPrompt.Should().Be("This is a connection test. Return one JSON object matching the schema and nothing else.");
        request.Messages[0].Content.Should().Be("Return {\"ok\":true}.");
        world.Models.Chosen.Should().Equal(new AiProviderSettings(AiProviderType.Mock));
        world.Plans.Plans.Writes.Should().Be(0);
        world.Plans.Audit.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("hallo", "not valid JSON")]
    [InlineData("{\"ok\":false}", "not usable")]
    [InlineData("{\"ok\":\"true\"}", "not usable")]
    [InlineData("{}", "not usable")]
    [InlineData("[true]", "not usable")]
    public async Task The_connection_test_reports_an_answer_that_is_not_ok_true_as_a_provider_failure(string answer, string reason)
    {
        var world = AiWorld.Replying((_, _) => answer);

        var result = await world.Service.TestConnectionAsync(new AiProviderSettings(AiProviderType.Mock), Ct);

        result.AsT3.Message.Should().StartWith("The AI connection worked, but the test answer was ").And.EndWith(reason);
    }

    [Fact]
    public async Task The_connection_test_validates_the_settings_before_choosing_a_model()
    {
        var world = AiWorld.Replying((_, _) => "{\"ok\":true}");

        var tooFast = await world.Service.TestConnectionAsync(new AiProviderSettings(AiProviderType.Ollama, null, "qwen3:8b", 9), Ct);
        var badUrl = await world.Service.TestConnectionAsync(new AiProviderSettings(AiProviderType.Ollama, "not a url", "qwen3:8b"), Ct);
        var emptyModel = await world.Service.TestConnectionAsync(new AiProviderSettings(AiProviderType.Ollama, null, ""), Ct);

        tooFast.AsT1.Errors.Keys.Should().Equal("aiProvider.timeoutSeconds");
        badUrl.AsT1.Errors.Keys.Should().Equal("aiProvider.endpoint");
        emptyModel.AsT1.Errors.Keys.Should().Equal("aiProvider.model");
        world.Models.Chosen.Should().BeEmpty();
        world.Model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_connection_test_reports_a_disabled_provider_and_a_provider_failure()
    {
        var disabled = new AiWorld(ScriptedModel.Failing(Disabled));
        var failing = new AiWorld(ScriptedModel.Failing(new PortError("AI provider returned HTTP 500")));

        (await disabled.Service.TestConnectionAsync(new AiProviderSettings(AiProviderType.None), Ct)).AsT2.Reason.Should().Be(AiUnavailableReason.Disabled);
        (await failing.Service.TestConnectionAsync(new AiProviderSettings(AiProviderType.Mock), Ct)).AsT3.Message.Should().Be("AI provider returned HTTP 500");
    }

    // ---- prompt information

    [Fact]
    public async Task Prompt_info_shows_the_effective_prompts_of_the_stored_settings()
    {
        var world = AiWorld.Replying((_, _) => "{}");
        world.Plans.Settings.Document = world.Plans.Settings.Document! with { AiPrompts = new AiPrompts("Voorstel", "", "", "") };

        var info = (await world.Service.GetPromptInfoAsync(Ct)).AsT0;

        info.Actions.PlanProposal.System.Should().EndWith("Voorstel");
        info.Defaults.PlanProposal.System.Should().NotContain("Voorstel");
        info.Actions.PlanProposal.FixedPrompt.Should().Be(info.Actions.PlanProposal.System);
    }

    [Fact]
    public async Task Prompt_info_answers_settings_missing_and_passes_on_a_failing_store()
    {
        var world = AiWorld.Replying((_, _) => "{}");
        world.Plans.Settings.Document = null;
        var missing = await world.Service.GetPromptInfoAsync(Ct);
        world.Plans.Settings.Document = SettingsSamples.Seeded();
        world.Plans.Settings.Failure = new PortError("settings down");
        var failing = await world.Service.GetPromptInfoAsync(Ct);

        missing.IsT1.Should().BeTrue();
        failing.AsT2.Message.Should().Be("settings down");
    }
}
