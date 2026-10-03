using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Ai;

/// <summary>
/// <c>/api/v2/ai</c> (requirements 5, 8). The prompt information needs no profile, like in the Node server; the connection test, proposals,
/// rebalancing, suggestions and explanations need a planner (<c>requirePlanner</c> there, <see cref="AuthorizationPolicies.PlannerPolicy"/> here):
/// they cost provider usage and should be attributable. Proposals are drafts; suggestions and explanations are stored nowhere.
/// </summary>
public static class AiEndpoints
{
    public const string AiTag = "AI";

    private const string Path = "/api/v2/ai";

    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path + "/prompt-info", PromptInfoAsync)
            .WithName("getAiPromptInfo")
            .WithTags(AiTag)
            .WithSummary("The prompts of the four AI use cases.")
            .WithDescription("Needs no profile. Per use case the effective template (the stored template, else the built-in one with the household's instructions appended), the fixed prompt and a description of the data the code adds; and the built-in defaults, so a template can be restored. Answers 500 settings_missing when the installation has no settings.")
            .Produces<AiPromptInfoResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/test", TestAsync)
            .RequirePlanner()
            .WithName("testAiProvider")
            .WithTags(AiTag)
            .WithSummary("Tests the given provider settings with a small structured request (planners).")
            .WithDescription("The body holds the provider settings to test, which need not be stored; the API key is the environment variable AI_API_KEY. Nothing is stored. Answers 400 validation_error for settings that break the rules (before any provider is contacted), 503 ai_disabled for the provider none, 503 ai_misconfigured for incomplete settings or a missing key, and 502 ai_provider_error when the provider failed or did not answer as asked.")
            .Accepts<TestAiProviderRequest>("application/json")
            .Produces<AiTestResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        routes.MapPost(Path + "/propose-plan", ProposeAsync)
            .RequirePlanner()
            .WithName("proposeAiPlan")
            .WithTags(AiTag)
            .WithSummary("Asks the AI for a plan and stores it as an inactive draft (planners).")
            .WithDescription("The body is optional: taskIds selects the tasks to plan (all active tasks when omitted), constraints holds free-text wishes. The answer is validated with the rules of the plan editor; when it fails the model is asked once more with the errors, after which 422 ai_invalid_plan carries the messages in errors and nothing is stored. A valid proposal is stored as an inactive draft (draft: true, source: ai) and audited with source ai; it is never activated. Answers 400 validation_error for a bad body, unknown or inactive tasks (unknown_task) or no active tasks (no_tasks), 422 ai_invalid_plan, 502 ai_provider_error, 503 ai_disabled and 503 ai_misconfigured.")
            .Accepts<ProposePlanRequest>("application/json")
            .Produces<AiProposalResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        routes.MapPost(Path + "/rebalance", RebalanceAsync)
            .RequirePlanner()
            .WithName("rebalanceAiPlan")
            .WithTags(AiTag)
            .WithSummary("Asks the AI to rebalance a plan and stores the result as an inactive draft (planners).")
            .WithDescription("Like a proposal, starting from the slots of the given plan: the draft is named after it, keeps its week themes and is audited with the base plan in meta.basePlanId. Answers 404 not_found for an unknown plan, and the same problems as a proposal.")
            .Accepts<RebalancePlanRequest>("application/json")
            .Produces<AiProposalResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        routes.MapPost(Path + "/suggest-tasks", SuggestTasksAsync)
            .RequirePlanner()
            .WithName("suggestAiTasks")
            .WithTags(AiTag)
            .WithSummary("Suggests tasks that are missing for a room (planners).")
            .WithDescription("Nothing is stored. Suggestions with an unknown interval, a duration that is not a whole number of minutes of at least 1, an empty name or a name that already exists in the room (case-insensitive, or earlier in the answer) are dropped. Answers 404 not_found for an unknown room, 422 ai_invalid_response when the answer was not usable, 502 ai_provider_error, 503 ai_disabled and 503 ai_misconfigured.")
            .Accepts<SuggestTasksRequest>("application/json")
            .Produces<AiSuggestionsResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        routes.MapPost(Path + "/explain", ExplainAsync)
            .RequirePlanner()
            .WithName("explainAiPlan")
            .WithTags(AiTag)
            .WithSummary("Explains a plan in four sentences, one per week (planners).")
            .WithDescription("Nothing is stored. Answers 404 not_found for an unknown plan, 422 ai_invalid_response when the answer did not hold exactly four sentences, 502 ai_provider_error, 503 ai_disabled and 503 ai_misconfigured.")
            .Accepts<ExplainPlanRequest>("application/json")
            .Produces<AiExplanationResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return routes;
    }

    private static async Task<IResult> PromptInfoAsync(IAiService ai, ILogger<IAiService> logger, CancellationToken cancellationToken)
    {
        var result = await ai.GetPromptInfoAsync(cancellationToken);
        return result.Match(
            info => Results.Ok(AiPromptInfoResponse.From(info)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> TestAsync(HttpContext http, IAiService ai, ILogger<IAiService> logger, CancellationToken cancellationToken)
    {
        var body = await AiRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (AiRequestParser.ParseTest(json).TryPickT1(out var invalid, out var provider))
        {
            return ProblemResults.From(invalid);
        }

        var result = await ai.TestConnectionAsync(provider, cancellationToken);
        return result.Match(
            _ => Results.Ok(new AiTestResponse(true)),
            ProblemResults.From,
            AiProblems.From,
            AiProblems.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> ProposeAsync(HttpContext http, IAiService ai, ILogger<IAiService> logger, CancellationToken cancellationToken)
    {
        var body = await AiRequestParser.ReadOptionalBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (AiRequestParser.ParsePropose(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        var result = await ai.ProposePlanAsync(await ActorOf(http), command, cancellationToken);
        return ProposalResult(result, logger);
    }

    private static async Task<IResult> RebalanceAsync(HttpContext http, IAiService ai, ILogger<IAiService> logger, CancellationToken cancellationToken)
    {
        var body = await AiRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (AiRequestParser.ParseRebalance(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        var result = await ai.RebalancePlanAsync(await ActorOf(http), command, cancellationToken);
        return ProposalResult(result, logger);
    }

    private static IResult ProposalResult(
        OneOf<PlanProposal, NotFound, ValidationErrors, SettingsMissing, AiUnavailable, AiProviderFailure, AiInvalidPlan, ConflictError, PortError> result,
        ILogger<IAiService> logger) =>
        result.Match(
            proposal => Results.Ok(AiProposalResponse.From(proposal)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            AiProblems.From,
            AiProblems.From,
            AiProblems.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));

    private static async Task<IResult> SuggestTasksAsync(HttpContext http, IAiService ai, ILogger<IAiService> logger, CancellationToken cancellationToken)
    {
        var body = await AiRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (AiRequestParser.ParseRoomId(json).TryPickT1(out var invalid, out var roomId))
        {
            return ProblemResults.From(invalid);
        }

        var result = await ai.SuggestTasksAsync(roomId, cancellationToken);
        return result.Match(
            suggestions => Results.Ok(new AiSuggestionsResponse([.. suggestions.Select(TaskSuggestionResponse.From)])),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            AiProblems.From,
            AiProblems.From,
            AiProblems.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> ExplainAsync(HttpContext http, IAiService ai, ILogger<IAiService> logger, CancellationToken cancellationToken)
    {
        var body = await AiRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (AiRequestParser.ParsePlanId(json).TryPickT1(out var invalid, out var planId))
        {
            return ProblemResults.From(invalid);
        }

        var result = await ai.ExplainPlanAsync(planId, cancellationToken);
        return result.Match(
            rationale => Results.Ok(new AiExplanationResponse(rationale)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            AiProblems.From,
            AiProblems.From,
            AiProblems.From,
            error => ProblemResults.From(error, logger));
    }

    /// <summary>The policy guarantees an actor on every proposal; a missing one is a programming error and becomes a 500.</summary>
    private static async Task<Actor> ActorOf(HttpContext http) =>
        await http.GetActorAsync() ?? throw new InvalidOperationException("An AI proposal ran without an actor; the endpoint must require authorization.");
}
