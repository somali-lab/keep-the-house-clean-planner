using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Planning;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The AI assistant (requirements 5): prompt information, a connection test, plan proposals and rebalancing (stored as drafts), task
/// suggestions and plan explanations (stored nowhere). Who may call what (everyone reads the prompt information, planners use the assistant) is
/// decided by the driving adapter; the <see cref="Actor"/> is attribution for the audit entry of a stored draft.
/// </summary>
public interface IAiService
{
    /// <summary>The effective prompts and the data the code adds to them. <see cref="SettingsMissing"/> when the installation has no settings.</summary>
    Task<OneOf<AiPromptInfo, SettingsMissing, PortError>> GetPromptInfoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks the model of the given (not necessarily stored) provider settings for a tiny structured answer and stores nothing. Settings that
    /// break the rules (a timeout out of range, an invalid endpoint) are a <see cref="ValidationErrors"/> before any provider is contacted.
    /// </summary>
    Task<OneOf<Success, ValidationErrors, AiUnavailable, AiProviderFailure, PortError>> TestConnectionAsync(AiProviderSettings provider, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the model for a plan for the active tasks (or the selection), validates it with the rules of the plan editor and re-prompts once with
    /// the errors. A valid proposal is stored as an inactive draft with an audit entry of source <c>ai</c>; it is never activated.
    /// </summary>
    Task<OneOf<PlanProposal, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidPlan, ConflictError, PortError>> ProposePlanAsync(
        Actor actor, ProposePlanCommand command, CancellationToken cancellationToken);

    /// <summary>Like a proposal, starting from the slots of the plan to rebalance; <see cref="NotFound"/> when that plan does not exist.</summary>
    Task<OneOf<PlanProposal, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidPlan, ConflictError, PortError>> RebalancePlanAsync(
        Actor actor, RebalancePlanCommand command, CancellationToken cancellationToken);

    /// <summary>Tasks that are missing for a room. Nothing is stored. <see cref="NotFound"/> for an unknown room.</summary>
    Task<OneOf<IReadOnlyList<TaskSuggestion>, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidResponse, PortError>> SuggestTasksAsync(
        string roomId, CancellationToken cancellationToken);

    /// <summary>A short rationale per week (four sentences) for a plan. Nothing is stored. <see cref="NotFound"/> for an unknown plan.</summary>
    Task<OneOf<IReadOnlyList<string>, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidResponse, PortError>> ExplainPlanAsync(
        string planId, CancellationToken cancellationToken);
}

/// <summary>A proposal request: the tasks to plan (all active tasks when <see langword="null"/>) and free-text wishes (at most 2000 characters).</summary>
public sealed record ProposePlanCommand(IReadOnlyList<string>? TaskIds = null, string? Constraints = null);

public sealed record RebalancePlanCommand(string PlanId, string? Constraints = null);

/// <summary>A stored AI draft: the plan, its proposal id, the non-blocking warnings of the validation and the rationale per week.</summary>
public sealed record PlanProposal(CyclePlan Plan, string ProposalId, IReadOnlyList<PlanIssue> Warnings, IReadOnlyList<string> Rationale);
