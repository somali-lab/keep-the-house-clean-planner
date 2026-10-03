using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.OpenApi;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Jobs;

/// <summary>
/// The manual triggers of the scheduled jobs, <c>/api/v2/jobs/*</c> (requirements 4.10, 8). Planners start them (<c>requirePlanner</c> in the Node server,
/// <see cref="AuthorizationPolicies.PlannerPolicy"/> here). A manual run is a human action: its audit entries carry the profile as actor. Like in the Node
/// server there is no overlap guard on a manual run: generation is idempotent and one transaction, so a run that meets the nightly run (or another manual run)
/// answers the conflict of the store (409) instead of corrupting anything. The morning notification (<c>/jobs/morning-notify</c>) comes with slice 6.3b.
/// </summary>
public static class JobEndpoints
{
    private const string Path = "/api/v2/jobs";

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost(Path + "/generation", GenerateAsync)
            .RequirePlanner()
            .WithName("runGeneration")
            .WithTags(OpenApiSetup.JobsTag)
            .WithSummary("Generates the current and the next cycle now (planners).")
            .WithDescription("Does the generation part of the nightly job: the occurrences of the current and the next cycle are generated from the active plan, idempotently, and open generated occurrences that no longer match the active plan are replaced. The audit entries carry the requesting profile as actor (source ui) and the run id in meta.runId. It never touches the points ledger. Answers 500 settings_missing when the installation has no settings, and 409 when another change to the same data is running.")
            .Produces<GenerationRunResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/audit-retention", RetainAsync)
            .RequirePlanner()
            .WithName("runAuditRetention")
            .WithTags(OpenApiSetup.JobsTag)
            .WithSummary("Applies the configured audit retention now (planners).")
            .WithDescription("Deletes the audit entries older than AUDIT_RETENTION_DAYS and answers the cutoff and the number deleted; without that setting it answers status disabled and deletes nothing. Writes no audit entry.")
            .Produces<AuditRetentionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> GenerateAsync(HttpContext http, IGenerationService generation, ILogger<IGenerationService> logger, CancellationToken cancellationToken)
    {
        // The policy guarantees an actor; a missing one is a programming error and becomes a 500.
        var actor = await http.GetActorAsync() ?? throw new InvalidOperationException("A manual job ran without an actor; the endpoint must require authorization.");
        var result = await generation.GenerateUpcomingAsync(AuditActor.From(actor), GenerationRunIds.New(), cancellationToken);
        return result.Match(
            run => Results.Ok(GenerationRunResponse.From(run)),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> RetainAsync(IAuditRetentionService retention, ILogger<IAuditRetentionService> logger, CancellationToken cancellationToken)
    {
        var result = await retention.RunAsync(cancellationToken);
        return result.Match(
            _ => Results.Ok(AuditRetentionResponse.Disabled()),
            done => Results.Ok(AuditRetentionResponse.Done(done)),
            error => ProblemResults.From(error, logger));
    }
}
