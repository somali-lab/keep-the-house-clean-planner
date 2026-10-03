using System.Text.Json.Nodes;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Domain.Ai;

/// <summary>A built prompt: the system part and the user part of the request.</summary>
public sealed record BuiltPrompt(string System, string User);

/// <summary>
/// The prompts and JSON schemas of the AI use cases (port of <c>apps/server/src/domain/ai/prompt.ts</c>, text for text). The templates
/// carry the placeholders <c>{{schema}}</c> (the JSON schema of the answer) and <c>{{input}}</c> (the request data as JSON); the household's
/// own instructions are appended to the system part unless a stored template replaces the built-in one.
/// </summary>
public static class PromptBuilder
{
    public const string SchemaPlaceholder = "{{schema}}";

    public const string InputPlaceholder = "{{input}}";

    /// <summary>The schema of the connection test answer.</summary>
    public const string ConnectionTestSchema = """{"type":"object","properties":{"ok":{"type":"boolean","const":true}},"required":["ok"],"additionalProperties":false}""";

    public const string ConnectionTestSystem = "This is a connection test. Return one JSON object matching the schema and nothing else.";

    public const string ConnectionTestUser = "Return {\"ok\":true}.";

    /// <summary>The JSON schema of the task suggestions answer (what <c>z.toJSONSchema</c> makes of the Node schema).</summary>
    public const string TaskSuggestionsSchema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"suggestions":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"intervalKey":{"type":"string"},"durationMinutes":{"type":"number"},"notes":{"type":"string"}},"required":["name","intervalKey","durationMinutes"],"additionalProperties":false}}},"required":["suggestions"],"additionalProperties":false}""";

    /// <summary>The JSON schema of the explanation answer.</summary>
    public const string ExplanationSchema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"rationale":{"type":"array","prefixItems":[{"type":"string","minLength":1},{"type":"string","minLength":1},{"type":"string","minLength":1},{"type":"string","minLength":1}],"items":false,"minItems":4,"maxItems":4}},"required":["rationale"],"additionalProperties":false}""";

    public static string PlanSystemPrompt { get; } = """
        You plan household chores for a household of the people listed in the input.
        The plan is a repeating 4-week cycle. weekIndex is 0..3. weekday is 0=Sunday, 1=Monday … 6=Saturday.

        Hard rules (a plan that breaks one is rejected):
        - Only use task ids and user ids from the input.
        - Never assign a slot to a user on a weekday listed in that user's unavailableWeekdays.
        - Never place the same task twice on the same day (same weekIndex and weekday).
        - Assign every slot to one concrete available user. Never use assigneeId null or an "either person" assignment.
        - If one person is unavailable on a weekday, a shared/either-person assignment is not a workaround: assign the task to another available person or use another day.

        Goals, in order:
        1. Place each task exactly perCycle times; tasks with perCycle null are optional.
        2. Keep each person's total minutes per week within two shared budgets: the weekday budget is the total for Monday–Friday together, and the weekend budget is the total for Saturday–Sunday together.
        3. Keep each individual day's minutes within maxDailyMinutes: use weekday for Monday–Friday and weekend for Saturday–Sunday.
        4. Balance total minutes per week between people.
        5. Spread repeats of the same task evenly over the cycle.
        6. Respect the free-text constraints (they are written in Dutch).
        7. Soft preference, ranked below availability, the intervals and the hard daily limits: keep recurring activities on the same weekdays and in a recognizable rhythm where possible (for example the same task on the same weekday in each of its repeats). Never break a hard rule, miss a perCycle count or exceed a daily limit for the sake of rhythm.
        In "rebalance" mode, start from currentSlots and change only what improves fairness, spread or budgets.
        """.ReplaceLineEndings("\n");

    public static string TaskSuggestionsSystem { get; } = """
        You help a household keep a complete list of recurring chores.
        Suggest chores for the given room that are missing from existingTasks.
        Use only interval keys from "intervals". durationMinutes is a realistic whole number of minutes (at least 1).
        Names and notes are in Dutch, short and concrete (e.g. "Afzuigkap ontvetten").
        Answer with one JSON object and nothing else, matching this JSON schema:
        {{schema}}
        """.ReplaceLineEndings("\n");

    public static string ExplanationSystem { get; } = """
        You explain a 4-week household chore plan to the people who follow it.
        weekIndex is 0..3, weekday is 0=Sunday … 6=Saturday, assignee null means "either person".
        Write exactly 4 short Dutch sentences, one per week, about how that week is divided and why it is fair or where it is busy.
        Answer with one JSON object and nothing else, matching this JSON schema:
        {{schema}}
        """.ReplaceLineEndings("\n");

    private static AiPromptTemplate PlanTemplate() => new(
        PlanSystemPrompt + "\n\nAnswer with one JSON object and nothing else, matching this JSON schema:\n{{schema}}\n\"rationale\" has exactly 4 short Dutch sentences, one per week, explaining the choices for that week.",
        InputPlaceholder);

    private static AiPromptTemplate TaskSuggestionsTemplate() => new(TaskSuggestionsSystem, InputPlaceholder);

    private static AiPromptTemplate ExplanationTemplate() => new(ExplanationSystem, InputPlaceholder);

    /// <summary>The built-in templates of the four use cases (restorable defaults).</summary>
    public static AiPromptTemplates DefaultTemplates() => new(PlanTemplate(), PlanTemplate(), TaskSuggestionsTemplate(), ExplanationTemplate());

    /// <summary>
    /// The JSON schema of a plan answer, with the ids narrowed to this household so a structured-output provider cannot confuse task and
    /// person ids.
    /// </summary>
    public static string PlanSchema(IEnumerable<string> taskIds, IEnumerable<string> userIds)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        ArgumentNullException.ThrowIfNull(userIds);
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["slots"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["taskId"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(taskIds) },
                            ["weekIndex"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 3 },
                            ["weekday"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 6 },
                            ["assigneeId"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(userIds) },
                            ["sortOrder"] = new JsonObject { ["type"] = "integer" },
                        },
                        ["required"] = Strings(["taskId", "weekIndex", "weekday", "assigneeId"]),
                        ["additionalProperties"] = false,
                    },
                },
                ["rationale"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                    ["minItems"] = 4,
                    ["maxItems"] = 4,
                },
            },
            ["required"] = Strings(["slots", "rationale"]),
            ["additionalProperties"] = false,
        };
        return schema.ToJsonString(PromptJson.Options);
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    /// <summary>The household's instructions appended to a system prompt; nothing when they are empty.</summary>
    public static string WithCustomInstructions(string system, string? customInstructions)
    {
        ArgumentNullException.ThrowIfNull(system);
        var custom = customInstructions?.Trim();
        return string.IsNullOrEmpty(custom)
            ? system
            : $"{system}\n\nAction-specific instructions configured by the household. Follow these when they do not conflict with the hard rules or required JSON format:\n{custom}";
    }

    private static string Render(string template, string token, string value) => template.Replace(token, value, StringComparison.Ordinal);

    /// <summary>The prompt of a plan request. <paramref name="template"/> defaults to the built-in one; custom instructions are appended to its system part.</summary>
    public static BuiltPrompt BuildPlanPrompt(PlanPromptPayload payload, string schemaJson, string? customInstructions = null, AiPromptTemplate? template = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(schemaJson);
        var chosen = template ?? PlanTemplate();
        return new BuiltPrompt(
            WithCustomInstructions(Render(chosen.System, SchemaPlaceholder, schemaJson), customInstructions),
            Render(chosen.User, InputPlaceholder, PromptJson.Serialize(payload)));
    }

    public static BuiltPrompt BuildTaskSuggestionsPrompt(TaskSuggestionPayload payload, string? customInstructions = null, AiPromptTemplate? template = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var chosen = template ?? TaskSuggestionsTemplate();
        return new BuiltPrompt(
            WithCustomInstructions(Render(chosen.System, SchemaPlaceholder, TaskSuggestionsSchema), customInstructions),
            Render(chosen.User, InputPlaceholder, PromptJson.Serialize(payload)));
    }

    public static BuiltPrompt BuildExplanationPrompt(ExplanationPayload payload, string? customInstructions = null, AiPromptTemplate? template = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var chosen = template ?? ExplanationTemplate();
        return new BuiltPrompt(
            WithCustomInstructions(Render(chosen.System, SchemaPlaceholder, ExplanationSchema), customInstructions),
            Render(chosen.User, InputPlaceholder, PromptJson.Serialize(payload)));
    }

    private const string PlanOutput =
        "De code eist daarna één JSON-object met alle indelingen en precies vier Nederlandse toelichtingszinnen. Het JSON-schema wordt dynamisch beperkt tot de actuele taak- en persoon-id’s en vereist per taak een concrete uitvoerder.";

    /// <summary>
    /// What <c>GET /ai/prompt-info</c> shows: per use case the effective template (a stored template, else the built-in one with the
    /// household's instructions appended), the fixed prompt and a description of the data the code adds; and the built-in defaults.
    /// </summary>
    public static AiPromptInfo CodeInfo(AiPrompts? legacy, AiPromptTemplates? configured)
    {
        var defaults = DefaultTemplates();
        var effective = configured ?? new AiPromptTemplates(
            defaults.PlanProposal with { System = WithCustomInstructions(defaults.PlanProposal.System, legacy?.PlanProposal) },
            defaults.PlanRebalance with { System = WithCustomInstructions(defaults.PlanRebalance.System, legacy?.PlanRebalance) },
            defaults.TaskSuggestions with { System = WithCustomInstructions(defaults.TaskSuggestions.System, legacy?.TaskSuggestions) },
            defaults.PlanExplanation with { System = WithCustomInstructions(defaults.PlanExplanation.System, legacy?.PlanExplanation) });
        return new AiPromptInfo(
            new AiPromptActions(
                Action(effective.PlanProposal, "modus, alle actieve taken met ruimte/cyclus/frequentie/duur, alle actieve personen met niet-beschikbare dagen en minuutlimieten, vrije wensen en bij een tweede poging de validatiefouten. " + PlanOutput),
                Action(effective.PlanRebalance, "Dezelfde gegevens als bij een voorstel, plus alle huidige indelingen van het actieve plan. " + PlanOutput),
                Action(effective.TaskSuggestions, "De gekozen ruimte, bestaande taken in die ruimte, actieve taken in andere ruimtes en alle toegestane cycli."),
                Action(effective.PlanExplanation, "Plannaam, actieve personen met hun minuutlimieten en iedere indeling met taak, week, weekdag, uitvoerder en duur.")),
            defaults);
    }

    private static AiPromptAction Action(AiPromptTemplate template, string dynamicData) => new(template.System, template.User, template.System, dynamicData);
}

/// <summary>One use case in the prompt information: the template, the fixed prompt (its system part) and the data the code adds.</summary>
public sealed record AiPromptAction(string System, string User, string FixedPrompt, string DynamicData);

public sealed record AiPromptActions(AiPromptAction PlanProposal, AiPromptAction PlanRebalance, AiPromptAction TaskSuggestions, AiPromptAction PlanExplanation);

public sealed record AiPromptInfo(AiPromptActions Actions, AiPromptTemplates Defaults);
