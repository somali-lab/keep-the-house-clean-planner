using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.CyclePlans;

/// <summary>
/// The activation of a cycle plan (requirements 4.3, 8): the preview and the activation, both for planners (<c>requirePlanner</c> in the Node
/// server, <see cref="AuthorizationPolicies.PlannerPolicy"/> here). Node: <c>GET /cycle-plans/:id/activation-preview</c> and
/// <c>POST /cycle-plans/:id/activate</c>; v2 names the action as a sub-resource, <c>POST .../activation</c>.
/// </summary>
public static class ActivationEndpoints
{
    private const string Path = "/api/v2/cycle-plans/{id}";

    public static IEndpointRouteBuilder MapActivationEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path + "/activation-preview", PreviewAsync)
            .RequirePlanner()
            .WithName("previewCyclePlanActivation")
            .WithTags(CyclePlanEndpoints.CyclePlansTag)
            .WithSummary("Previews what activating a plan would change (planners).")
            .WithDescription("Read-only: nothing is written or audited. For the current and the next cycle it lists the open generated occurrences that activation removes, the occurrences it adds, and what stays (done, skipped, moved by hand, ad hoc). previewToken fingerprints the plan, the tasks and settings it uses and the occurrences of the window; send it to the activation. Answers 404 not_found for an unknown plan and 500 settings_missing when the installation has no settings.")
            .Produces<ActivationPreviewResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/activation", ActivateAsync)
            .RequirePlanner()
            .WithName("activateCyclePlan")
            .WithTags(CyclePlanEndpoints.CyclePlansTag)
            .WithSummary("Activates a plan after its preview (planners).")
            .WithDescription("One transaction: the preview is recomputed and its token compared first; a different token answers 409 stale_activation_preview and nothing is written, so the client fetches a new preview. Otherwise every other active plan is deactivated (audited as update), the plan is activated (audited as activate; an AI draft stops being a draft) and the open generated occurrences of the current and the next cycle are replaced by those of the plan (audited with the system as source and the same runId). Activating the active plan again regenerates its upcoming occurrences. Two concurrent activations are serialised: one wins, the other answers 409 stale_activation_preview. Answers 404 not_found for an unknown plan.")
            .Accepts<ActivatePlanRequest>("application/json")
            .Produces<PlanActivatedResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> PreviewAsync(string id, IActivationService activation, ILogger<IActivationService> logger, CancellationToken cancellationToken)
    {
        var result = await activation.PreviewAsync(id, cancellationToken);
        return result.Match(
            preview => Results.Ok(ActivationPreviewResponse.From(preview)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> ActivateAsync(string id, HttpContext http, IActivationService activation, ILogger<IActivationService> logger, CancellationToken cancellationToken)
    {
        var body = await ActivationRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (ActivationRequestParser.ParseToken(json).TryPickT1(out var invalid, out var token))
        {
            return ProblemResults.From(invalid);
        }

        var actor = await http.GetActorAsync() ?? throw new InvalidOperationException("An activation ran without an actor; the endpoint must require authorization.");
        var result = await activation.ActivateAsync(actor, id, token, cancellationToken);
        return result.Match(
            activated => Results.Ok(PlanActivatedResponse.From(activated)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }
}
