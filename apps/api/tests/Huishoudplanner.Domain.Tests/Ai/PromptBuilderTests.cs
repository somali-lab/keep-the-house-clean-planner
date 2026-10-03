using System.Text.Json;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Tests.Ai;

/// <summary>The prompts and schemas of prompt.ts, text for text.</summary>
public class PromptBuilderTests
{
    private static readonly PlanPromptPayload Payload = new(
        PlanModes.Propose,
        [new PromptTask("a".PadRight(24, 'a'), "Badkamer schoonmaken", "Badkamer", "1w", "1x per week", 4, 7, 30), new PromptTask("b".PadRight(24, 'b'), "Optioneel", null, "quarter", "1x per kwartaal", null, 91, 10)],
        [new PromptUser("c".PadRight(24, 'c'), "Persoon 1", [2], new PromptMinutes(60, 120), new PromptMinutes(120, 240))]);

    private static string Schema => PromptBuilder.PlanSchema([Payload.Tasks[0].Id, Payload.Tasks[1].Id], [Payload.Users[0].Id]);

    [Fact]
    public void The_plan_user_message_is_the_payload_as_compact_json_in_the_documented_key_order()
    {
        var prompt = PromptBuilder.BuildPlanPrompt(Payload, Schema);

        prompt.User.Should().Be(
            "{\"mode\":\"propose\",\"tasks\":[{\"id\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"name\":\"Badkamer schoonmaken\",\"room\":\"Badkamer\",\"intervalKey\":\"1w\",\"intervalLabel\":\"1x per week\",\"perCycle\":4,\"periodDays\":7,\"durationMinutes\":30},"
            + "{\"id\":\"bbbbbbbbbbbbbbbbbbbbbbbb\",\"name\":\"Optioneel\",\"room\":null,\"intervalKey\":\"quarter\",\"intervalLabel\":\"1x per kwartaal\",\"perCycle\":null,\"periodDays\":91,\"durationMinutes\":10}],"
            + "\"users\":[{\"id\":\"cccccccccccccccccccccccc\",\"name\":\"Persoon 1\",\"unavailableWeekdays\":[2],\"dailyBudgetMinutes\":{\"weekday\":60,\"weekend\":120},\"maxDailyMinutes\":{\"weekday\":120,\"weekend\":240}}]}");
    }

    [Fact]
    public void Optional_parts_of_the_payload_are_left_out_unless_set_and_a_re_prompt_adds_the_errors_last()
    {
        var full = Payload with
        {
            Mode = PlanModes.Rebalance,
            CurrentSlots = [new PromptSlot("a".PadRight(24, 'a'), 0, 1, null)],
            Constraints = "geen nat werk",
            PreviousErrors = ["fout één"],
        };

        var json = JsonDocument.Parse(PromptBuilder.BuildPlanPrompt(full, Schema).User).RootElement;

        json.EnumerateObject().Select(p => p.Name).Should().Equal("mode", "tasks", "users", "currentSlots", "constraints", "previousErrors");
        json.GetProperty("currentSlots")[0].GetProperty("assigneeId").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("previousErrors")[0].GetString().Should().Be("fout één");
        JsonDocument.Parse(PromptBuilder.BuildPlanPrompt(Payload, Schema).User).RootElement.TryGetProperty("constraints", out _).Should().BeFalse();
    }

    [Fact]
    public void The_default_plan_system_prompt_holds_the_hard_rules_the_goals_and_the_schema()
    {
        var system = PromptBuilder.BuildPlanPrompt(Payload, Schema).System;

        system.Should().StartWith("You plan household chores for a household of the people listed in the input.\nThe plan is a repeating 4-week cycle.");
        system.Should().Contain("- Only use task ids and user ids from the input.")
            .And.Contain("Never assign a slot to a user on a weekday listed in that user's unavailableWeekdays.")
            .And.Contain("Never use assigneeId null or an \"either person\" assignment.")
            .And.Contain("1. Place each task exactly perCycle times; tasks with perCycle null are optional.")
            .And.Contain("In \"rebalance\" mode, start from currentSlots and change only what improves fairness, spread or budgets.")
            .And.Contain("Answer with one JSON object and nothing else, matching this JSON schema:\n" + Schema + "\n\"rationale\" has exactly 4 short Dutch sentences, one per week, explaining the choices for that week.");
        system.Should().NotContain("{{schema}}");
        system.Should().NotContain("\r");
    }

    [Fact]
    public void The_rhythm_goal_is_the_last_goal_and_ranked_below_the_hard_limits()
    {
        var lines = PromptBuilder.PlanSystemPrompt.Split('\n');

        var rhythm = Array.FindIndex(lines, l => l.StartsWith("7. Soft preference, ranked below availability, the intervals and the hard daily limits", StringComparison.Ordinal));
        rhythm.Should().BeGreaterThan(Array.FindIndex(lines, l => l.StartsWith("6. Respect the free-text constraints", StringComparison.Ordinal)));
        lines[rhythm].Should().Contain("same weekdays").And.Contain("Never break a hard rule");
    }

    [Fact]
    public void Household_instructions_are_appended_to_the_system_part_only_when_they_are_not_empty()
    {
        var withCustom = PromptBuilder.BuildPlanPrompt(Payload, Schema, "  Plan zware taken nooit na elkaar.  ");
        var withBlank = PromptBuilder.BuildPlanPrompt(Payload, Schema, "   ");
        var none = PromptBuilder.BuildPlanPrompt(Payload, Schema);

        withCustom.System.Should().EndWith(
            "\n\nAction-specific instructions configured by the household. Follow these when they do not conflict with the hard rules or required JSON format:\nPlan zware taken nooit na elkaar.");
        withBlank.System.Should().Be(none.System);
    }

    [Fact]
    public void A_stored_template_replaces_the_built_in_one_and_its_placeholders_are_expanded()
    {
        var template = new AiPromptTemplate("Mijn systeemprompt {{schema}} en nog eens {{schema}}", "Invoer: {{input}}");

        var prompt = PromptBuilder.BuildPlanPrompt(Payload, Schema, null, template);

        prompt.System.Should().Be($"Mijn systeemprompt {Schema} en nog eens {Schema}");
        prompt.User.Should().StartWith("Invoer: {\"mode\":\"propose\"");
    }

    [Fact]
    public void The_plan_schema_narrows_the_ids_to_the_household()
    {
        var schema = JsonDocument.Parse(Schema).RootElement;

        var slot = schema.GetProperty("properties").GetProperty("slots").GetProperty("items");
        slot.GetProperty("properties").GetProperty("taskId").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal(Payload.Tasks[0].Id, Payload.Tasks[1].Id);
        slot.GetProperty("properties").GetProperty("assigneeId").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal(Payload.Users[0].Id);
        slot.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("taskId", "weekIndex", "weekday", "assigneeId");
        slot.GetProperty("properties").GetProperty("weekIndex").GetProperty("maximum").GetInt32().Should().Be(3);
        schema.GetProperty("properties").GetProperty("rationale").GetProperty("minItems").GetInt32().Should().Be(4);
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void The_task_suggestion_and_explanation_prompts_embed_their_schemas()
    {
        var suggestions = PromptBuilder.BuildTaskSuggestionsPrompt(new TaskSuggestionPayload("Keuken", [new ExistingTaskInfo("Aanrecht", "1w", 20)], [new OtherTaskInfo(null, "Douche")], [new PromptInterval("1w", "1x per week", 7)]));
        var explanation = PromptBuilder.BuildExplanationPrompt(new ExplanationPayload("Standaard", [new ExplanationUser("u", "P1", new PromptMinutes(1, 2), new PromptMinutes(3, 4))], [new ExplanationSlot("Douche", 0, 1, null, 30)]));

        suggestions.System.Should().StartWith("You help a household keep a complete list of recurring chores.")
            .And.EndWith("matching this JSON schema:\n" + PromptBuilder.TaskSuggestionsSchema);
        suggestions.User.Should().Be("{\"room\":\"Keuken\",\"existingTasks\":[{\"name\":\"Aanrecht\",\"intervalKey\":\"1w\",\"durationMinutes\":20}],\"otherTasks\":[{\"room\":null,\"name\":\"Douche\"}],\"intervals\":[{\"key\":\"1w\",\"label\":\"1x per week\",\"periodDays\":7}]}");
        explanation.System.Should().StartWith("You explain a 4-week household chore plan to the people who follow it.")
            .And.EndWith("matching this JSON schema:\n" + PromptBuilder.ExplanationSchema);
        explanation.User.Should().Be("{\"planName\":\"Standaard\",\"users\":[{\"id\":\"u\",\"name\":\"P1\",\"dailyBudgetMinutes\":{\"weekday\":1,\"weekend\":2},\"maxDailyMinutes\":{\"weekday\":3,\"weekend\":4}}],\"slots\":[{\"task\":\"Douche\",\"weekIndex\":0,\"weekday\":1,\"assignee\":null,\"durationMinutes\":30}]}");
    }

    [Fact]
    public void The_embedded_schemas_are_valid_json_and_the_connection_test_asks_for_ok_true()
    {
        foreach (var json in new[] { PromptBuilder.TaskSuggestionsSchema, PromptBuilder.ExplanationSchema, PromptBuilder.ConnectionTestSchema })
        {
            JsonDocument.Parse(json).RootElement.GetProperty("type").GetString().Should().Be("object");
        }

        JsonDocument.Parse(PromptBuilder.ConnectionTestSchema).RootElement.GetProperty("properties").GetProperty("ok").GetProperty("const").GetBoolean().Should().BeTrue();
        PromptBuilder.ConnectionTestUser.Should().Be("Return {\"ok\":true}.");
    }

    [Fact]
    public void The_default_templates_have_both_placeholders_so_the_settings_rules_accept_them()
    {
        var defaults = PromptBuilder.DefaultTemplates();

        foreach (var template in new[] { defaults.PlanProposal, defaults.PlanRebalance, defaults.TaskSuggestions, defaults.PlanExplanation })
        {
            template.System.Should().Contain(PromptBuilder.SchemaPlaceholder);
            template.User.Should().Be(PromptBuilder.InputPlaceholder);
        }
    }

    [Fact]
    public void The_code_info_shows_the_built_in_templates_with_the_household_instructions_when_no_template_is_stored()
    {
        var legacy = new AiPrompts("Voorstel-tekst", "", "Suggestie-tekst", "  ");

        var info = PromptBuilder.CodeInfo(legacy, null);

        info.Actions.PlanProposal.System.Should().EndWith("Follow these when they do not conflict with the hard rules or required JSON format:\nVoorstel-tekst");
        info.Actions.PlanRebalance.System.Should().Be(PromptBuilder.DefaultTemplates().PlanRebalance.System);
        info.Actions.TaskSuggestions.System.Should().EndWith("Suggestie-tekst");
        info.Actions.PlanExplanation.System.Should().Be(PromptBuilder.DefaultTemplates().PlanExplanation.System);
        info.Actions.PlanProposal.FixedPrompt.Should().Be(info.Actions.PlanProposal.System);
        info.Actions.PlanProposal.User.Should().Be("{{input}}");
        info.Actions.PlanProposal.DynamicData.Should().StartWith("modus, alle actieve taken met ruimte/cyclus/frequentie/duur").And.Contain("persoon-id’s");
        info.Actions.PlanRebalance.DynamicData.Should().StartWith("Dezelfde gegevens als bij een voorstel, plus alle huidige indelingen van het actieve plan.");
        info.Actions.TaskSuggestions.DynamicData.Should().Be("De gekozen ruimte, bestaande taken in die ruimte, actieve taken in andere ruimtes en alle toegestane cycli.");
        info.Actions.PlanExplanation.DynamicData.Should().StartWith("Plannaam, actieve personen");
        info.Defaults.PlanProposal.System.Should().Be(PromptBuilder.DefaultTemplates().PlanProposal.System);
    }

    [Fact]
    public void The_code_info_shows_a_stored_template_as_it_is_and_ignores_the_household_instructions()
    {
        var stored = new AiPromptTemplate("A {{schema}}", "B {{input}}");
        var templates = new AiPromptTemplates(stored, stored with { System = "R {{schema}}" }, stored, stored);

        var info = PromptBuilder.CodeInfo(new AiPrompts("negeer mij", "", "", ""), templates);

        info.Actions.PlanProposal.System.Should().Be("A {{schema}}");
        info.Actions.PlanProposal.User.Should().Be("B {{input}}");
        info.Actions.PlanRebalance.System.Should().Be("R {{schema}}");
        info.Defaults.PlanRebalance.System.Should().Contain("You plan household chores");
    }
}
