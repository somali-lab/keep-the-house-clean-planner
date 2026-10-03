using System.ComponentModel;
using Huishoudplanner.Adapters.Http.CyclePlans;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Adapters.Http.Ai;

// The request records below only document the bodies in OpenAPI; the bodies are read by AiRequestParser (Zod-style validation_error for
// a wrong type, null or malformed JSON), never bound to these types.

/// <summary>The provider settings to test. The API key is never part of them: it is the environment variable <c>AI_API_KEY</c>.</summary>
public sealed record AiProviderRequest(
    [property: Description("none, mock, anthropic, openai-compatible or ollama.")] string? Type,
    [property: Description("An absolute URL.")] string? Endpoint,
    string? Model,
    [property: Description("Whole seconds, 10 to 900.")] int? TimeoutSeconds);

/// <summary>The body of the connection test.</summary>
public sealed record TestAiProviderRequest(AiProviderRequest? AiProvider);

/// <summary>A plan proposal request. Both members are optional; an empty body proposes a plan for all active tasks.</summary>
public sealed record ProposePlanRequest(
    [property: Description("The tasks to plan, at least one id of an active task. Omitted: all active tasks.")] IReadOnlyList<string>? TaskIds,
    [property: Description("Free-text wishes, at most 2000 characters after trimming.")] string? Constraints);

/// <summary>A rebalance request.</summary>
public sealed record RebalancePlanRequest(
    [property: Description("The plan to rebalance; 404 not_found when it does not exist.")] string? PlanId,
    [property: Description("Free-text wishes, at most 2000 characters after trimming.")] string? Constraints);

public sealed record SuggestTasksRequest([property: Description("The room to suggest tasks for; 404 not_found when it does not exist.")] string? RoomId);

public sealed record ExplainPlanRequest([property: Description("The plan to explain; 404 not_found when it does not exist.")] string? PlanId);

/// <summary>One use case in the prompt information.</summary>
public sealed record AiPromptActionResponse(
    [property: Description("The system part of the effective template.")] string System,
    [property: Description("The user part of the effective template.")] string User,
    [property: Description("The fixed prompt: the system part.")] string FixedPrompt,
    [property: Description("A description of the data the code adds to the prompt.")] string DynamicData)
{
    internal static AiPromptActionResponse From(AiPromptAction action) => new(action.System, action.User, action.FixedPrompt, action.DynamicData);
}

public sealed record AiPromptActionsResponse(
    AiPromptActionResponse PlanProposal,
    AiPromptActionResponse PlanRebalance,
    AiPromptActionResponse TaskSuggestions,
    AiPromptActionResponse PlanExplanation);

/// <summary>A system and user template; <c>{{schema}}</c> and <c>{{input}}</c> are replaced when a prompt is built.</summary>
public sealed record AiPromptTemplateResponse(string System, string User);

public sealed record AiPromptDefaultsResponse(
    AiPromptTemplateResponse PlanProposal,
    AiPromptTemplateResponse PlanRebalance,
    AiPromptTemplateResponse TaskSuggestions,
    AiPromptTemplateResponse PlanExplanation);

/// <summary>The effective prompts per use case and the built-in defaults.</summary>
public sealed record AiPromptInfoResponse(AiPromptActionsResponse Actions, AiPromptDefaultsResponse Defaults)
{
    internal static AiPromptInfoResponse From(AiPromptInfo info) => new(
        new AiPromptActionsResponse(
            AiPromptActionResponse.From(info.Actions.PlanProposal),
            AiPromptActionResponse.From(info.Actions.PlanRebalance),
            AiPromptActionResponse.From(info.Actions.TaskSuggestions),
            AiPromptActionResponse.From(info.Actions.PlanExplanation)),
        new AiPromptDefaultsResponse(
            Template(info.Defaults.PlanProposal),
            Template(info.Defaults.PlanRebalance),
            Template(info.Defaults.TaskSuggestions),
            Template(info.Defaults.PlanExplanation)));

    private static AiPromptTemplateResponse Template(Domain.Settings.AiPromptTemplate template) => new(template.System, template.User);
}

/// <summary>The connection works and the model answered as asked.</summary>
public sealed record AiTestResponse(bool Ok);

/// <summary>A stored AI draft. The active plan does not change until the draft is activated.</summary>
public sealed record AiProposalResponse(
    [property: Description("The id of the draft plan (inactive, draft: true, source: ai).")] string PlanId,
    string ProposalId,
    [property: Description("The non-blocking warnings of the plan validation (interval_mismatch, over_budget, daily_over_budget).")] IReadOnlyList<PlanIssueResponse> Warnings,
    [property: Description("Four sentences, one per week.")] IReadOnlyList<string> Rationale)
{
    internal static AiProposalResponse From(PlanProposal proposal) =>
        new(proposal.Plan.Id, proposal.ProposalId, [.. proposal.Warnings.Select(PlanIssueResponse.From)], proposal.Rationale);
}

/// <summary>A suggested task; it is added to the task list only when the person chooses to.</summary>
public sealed record TaskSuggestionResponse(string Name, string IntervalKey, int DurationMinutes, string Notes)
{
    internal static TaskSuggestionResponse From(TaskSuggestion suggestion) =>
        new(suggestion.Name, suggestion.IntervalKey, suggestion.DurationMinutes, suggestion.Notes);
}

public sealed record AiSuggestionsResponse(IReadOnlyList<TaskSuggestionResponse> Suggestions);

public sealed record AiExplanationResponse([property: Description("Four sentences, one per week.")] IReadOnlyList<string> Rationale);
