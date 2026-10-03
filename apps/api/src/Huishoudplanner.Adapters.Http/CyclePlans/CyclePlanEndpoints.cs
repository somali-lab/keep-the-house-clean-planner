using System.Globalization;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.CyclePlans;

/// <summary>
/// <c>/api/v2/cycle-plans</c> (requirements 4.3, 8). Reads are open like in the Node server; create, change, delete, saving slots and
/// the two validation endpoints need a planner (<c>requirePlanner</c> there, <see cref="AuthorizationPolicies.PlannerPolicy"/> here).
/// The activation preview and the activation come with slice 2.4.
/// </summary>
public static class CyclePlanEndpoints
{
    public const string CyclePlansTag = "Cycle plans";

    private const string Path = "/api/v2/cycle-plans";

    public const string InvalidPlanCode = "invalid_plan";

    public static IEndpointRouteBuilder MapCyclePlanEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path, ListAsync)
            .WithName("listCyclePlans")
            .WithTags(CyclePlansTag)
            .WithSummary("Lists the cycle plans, oldest first.")
            .WithDescription("Needs no profile. The first plan is the default plan. The list is paged: pass the nextCursor of a page as cursor for the next one (limit 1 to 200, default 50).")
            .Produces<CyclePlanListResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/active", GetActiveAsync)
            .WithName("getActiveCyclePlan")
            .WithTags(CyclePlansTag)
            .WithSummary("The active cycle plan.")
            .WithDescription("Needs no profile. Answers 404 not_found when no plan is active.")
            .Produces<CyclePlanResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/{id}", GetAsync)
            .WithName("getCyclePlan")
            .WithTags(CyclePlansTag)
            .WithSummary("One cycle plan.")
            .WithDescription("Needs no profile. Answers 404 not_found for an unknown plan.")
            .Produces<CyclePlanResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path, CreateAsync)
            .RequirePlanner()
            .WithName("createCyclePlan")
            .WithTags(CyclePlansTag)
            .WithSummary("Creates an inactive cycle plan, empty or as a copy of another one (planners).")
            .WithDescription("With copyFromId the new plan gets the slots and week themes of that plan, and the audit entry names the source in meta.copiedFrom; an unknown source answers 404 not_found. The change is audited.")
            .Accepts<CreateCyclePlanRequest>("application/json")
            .Produces<CyclePlanResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPatch(Path + "/{id}", UpdateAsync)
            .RequirePlanner()
            .WithName("updateCyclePlan")
            .WithTags(CyclePlansTag)
            .WithSummary("Renames a plan or sets its week themes (planners).")
            .WithDescription("A change that changes nothing writes and audits nothing. Answers 404 not_found for an unknown plan.")
            .Accepts<UpdateCyclePlanRequest>("application/json")
            .Produces<CyclePlanResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapDelete(Path + "/{id}", DeleteAsync)
            .RequirePlanner()
            .WithName("deleteCyclePlan")
            .WithTags(CyclePlansTag)
            .WithSummary("Deletes a plan that is neither the default nor the active plan (planners).")
            .WithDescription("The oldest plan is the default plan: 409 default_plan. The active plan: 409 active_plan. Answers 404 not_found for an unknown plan. The deleted plan is audited.")
            .Produces<CyclePlanDeletedResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/{id}/diff", DiffAsync)
            .WithName("diffCyclePlan")
            .WithTags(CyclePlansTag)
            .WithSummary("Compares a plan with the active plan.")
            .WithDescription("Needs no profile. Slot-level differences (added, removed, moved, unchanged) per task against the active plan, plus the minutes per person per week before and after and the warnings of the plan. The optional query parameter against only accepts active. Answers 404 not_found for an unknown plan.")
            .Produces<PlanDiffResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPut(Path + "/{id}/slots", PutSlotsAsync)
            .RequirePlanner()
            .WithName("replaceCyclePlanSlots")
            .WithTags(CyclePlansTag)
            .WithSummary("Replaces all slots of a plan (planners).")
            .WithDescription("The plan is validated first: a plan that breaks a hard rule (unknown or inactive task or person, unavailable assignee, the same task twice on one day) is refused with 422 invalid_plan, carrying errors, issues, warnings and summary, and nothing is written. Otherwise the plan is saved and returned with the warnings and the summary. The audit entry holds only the added, removed and changed slots; saving what is stored writes and audits nothing.")
            .Accepts<PlanSlotsRequest>("application/json")
            .Produces<PlanSlotsSavedResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/{id}/validation", ValidateAsync)
            .RequirePlanner()
            .WithName("validateCyclePlan")
            .WithTags(CyclePlansTag)
            .WithSummary("Validates a stored plan, as a save of its slots would (planners).")
            .WithDescription("Reads only; nothing is written. Answers 200 with valid, the hard errors (issues, and errors keyed by field path), the warnings and the summary, also when the plan has errors. Answers 404 not_found for an unknown plan.")
            .Produces<PlanValidationResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/validation", ValidateDraftAsync)
            .RequirePlanner()
            .WithName("validateCyclePlanDraft")
            .WithTags(CyclePlansTag)
            .WithSummary("Validates an unsaved draft, as a save of these slots would (planners).")
            .WithDescription("The body holds the slots of the draft, like a save. Reads only; nothing is written. Answers 200 with valid, the hard errors (issues, and errors keyed by field path), the warnings and the summary, also when the draft has errors.")
            .Accepts<PlanSlotsRequest>("application/json")
            .Produces<PlanValidationResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        ICyclePlanService plans,
        ILogger<ICyclePlanService> logger,
        CancellationToken cancellationToken,
        string? limit = null,
        string? cursor = null)
    {
        // Bound as strings so that a malformed value is a field-keyed validation_error, not a framework binding failure.
        int? limitValue = null;
        if (limit is not null)
        {
            if (int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                limitValue = parsed;
            }
            else
            {
                return ProblemResults.From(ValidationErrors.For("limit", "Must be an integer."));
            }
        }

        var result = await plans.ListAsync(limitValue, cursor, cancellationToken);
        return result.Match(
            list => Results.Ok(new CyclePlanListResponse([.. list.Items.Select(CyclePlanResponse.From)], list.NextCursor)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> GetActiveAsync(ICyclePlanService plans, ILogger<ICyclePlanService> logger, CancellationToken cancellationToken)
    {
        var result = await plans.GetActiveAsync(cancellationToken);
        return result.Match(
            plan => Results.Ok(CyclePlanResponse.From(plan)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> GetAsync(string id, ICyclePlanService plans, ILogger<ICyclePlanService> logger, CancellationToken cancellationToken)
    {
        var result = await plans.GetAsync(id, cancellationToken);
        return result.Match(
            plan => Results.Ok(CyclePlanResponse.From(plan)),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> CreateAsync(HttpContext http, ICyclePlanService plans, ILogger<ICyclePlanService> logger, CancellationToken cancellationToken)
    {
        var body = await CyclePlanRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (CyclePlanRequestParser.ParseCreate(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        var result = await plans.CreateAsync(await ActorOf(http), command, cancellationToken);
        return result.Match(
            plan => Results.Json(CyclePlanResponse.From(plan), statusCode: StatusCodes.Status201Created),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> UpdateAsync(string id, HttpContext http, ICyclePlanService plans, ILogger<ICyclePlanService> logger, CancellationToken cancellationToken)
    {
        var body = await CyclePlanRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (CyclePlanRequestParser.ParsePatch(json).TryPickT1(out var invalid, out var patch))
        {
            return ProblemResults.From(invalid);
        }

        var result = await plans.UpdateAsync(await ActorOf(http), id, patch, cancellationToken);
        return result.Match(
            plan => Results.Ok(CyclePlanResponse.From(plan)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> DeleteAsync(string id, HttpContext http, ICyclePlanService plans, ILogger<ICyclePlanService> logger, CancellationToken cancellationToken)
    {
        var result = await plans.DeleteAsync(await ActorOf(http), id, cancellationToken);
        return result.Match(
            _ => Results.Ok(new CyclePlanDeletedResponse(true)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> DiffAsync(string id, ICyclePlanService plans, ILogger<ICyclePlanService> logger, CancellationToken cancellationToken, string? against = null)
    {
        if (against is not null and not "active")
        {
            return ProblemResults.From(ValidationErrors.For("against", "Must be 'active'."));
        }

        var result = await plans.CompareWithActiveAsync(id, cancellationToken);
        return result.Match(
            comparison => Results.Ok(PlanDiffResponse.From(comparison)),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> PutSlotsAsync(string id, HttpContext http, ICyclePlanService plans, ILogger<ICyclePlanService> logger, CancellationToken cancellationToken)
    {
        var body = await CyclePlanRequestParser.ReadSlotsBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (CyclePlanRequestParser.ParseSlots(json).TryPickT1(out var invalid, out var slots))
        {
            return ProblemResults.From(invalid);
        }

        var result = await plans.ReplaceSlotsAsync(await ActorOf(http), id, slots, cancellationToken);
        return result.Match(
            saved => Results.Ok(PlanSlotsSavedResponse.From(saved)),
            ProblemResults.From,
            ProblemResults.From,
            InvalidPlanProblem,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> ValidateAsync(string id, ICyclePlanService plans, ILogger<ICyclePlanService> logger, CancellationToken cancellationToken)
    {
        var result = await plans.ValidateAsync(id, cancellationToken);
        return result.Match(
            validation => Results.Ok(PlanValidationResponse.From(validation)),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> ValidateDraftAsync(HttpContext http, ICyclePlanService plans, ILogger<ICyclePlanService> logger, CancellationToken cancellationToken)
    {
        var body = await CyclePlanRequestParser.ReadSlotsBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (CyclePlanRequestParser.ParseSlots(json).TryPickT1(out var invalid, out var slots))
        {
            return ProblemResults.From(invalid);
        }

        var result = await plans.ValidateDraftAsync(slots, cancellationToken);
        return result.Match(
            validation => Results.Ok(PlanValidationResponse.From(validation)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    /// <summary>
    /// <c>422 invalid_plan</c> with the validation of the save as extension members (Node: <c>details</c>): the same <c>errors</c>,
    /// <c>issues</c>, <c>warnings</c> and <c>summary</c> as the validation endpoints.
    /// </summary>
    private static IResult InvalidPlanProblem(InvalidPlan invalid)
    {
        var validation = PlanValidationResponse.From(invalid.Validation);
        return ProblemResults.Problem(
            StatusCodes.Status422UnprocessableEntity,
            InvalidPlanCode,
            "Plan violates hard rules",
            new Dictionary<string, object?>
            {
                ["errors"] = validation.Errors,
                ["issues"] = validation.Issues,
                ["warnings"] = validation.Warnings,
                ["summary"] = validation.Summary,
            });
    }

    /// <summary>The policy guarantees an actor on every write; a missing one is a programming error and becomes a 500.</summary>
    private static async Task<Actor> ActorOf(HttpContext http) =>
        await http.GetActorAsync() ?? throw new InvalidOperationException("A cycle plan write ran without an actor; the endpoint must require authorization.");
}
