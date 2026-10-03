using System.ComponentModel;
using Huishoudplanner.Adapters.Http.CyclePlans;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Promotion;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Promotion;

/// <summary>Where a suggested change starts: the slot as it is in the active plan.</summary>
public sealed record PromoteFromSlotResponse(int WeekIndex, [property: Description("0 Sunday to 6 Saturday.")] int Weekday, string? AssigneeId);

/// <summary>A suggestion to move one slot of the active plan.</summary>
public sealed record PromoteSuggestionResponse(
    string PlanId,
    string TaskId,
    string TaskName,
    PromoteFromSlotResponse FromSlot,
    [property: Description("The weekday (0 Sunday to 6 Saturday) the occurrences were moved to in every one of the evidence cycles.")] int ToWeekday,
    [property: Description("Set only when every move also went to the same other person.")] string? ToAssigneeId,
    [property: Description("The moved occurrences, newest cycle first; the first is the newest evidence.")] IReadOnlyList<string> Evidence)
{
    internal static PromoteSuggestionResponse From(PromoteSuggestion suggestion) => new(
        suggestion.PlanId,
        suggestion.TaskId,
        suggestion.TaskName,
        new PromoteFromSlotResponse(suggestion.FromSlot.WeekIndex, suggestion.FromSlot.Weekday, suggestion.FromSlot.AssigneeId),
        suggestion.ToWeekday,
        suggestion.ToAssigneeId,
        suggestion.EvidenceIds);
}

/// <summary>Which slot of the active plan moves where. <c>weekIndex</c> 0 to 3, weekdays 0 (Sunday) to 6 (Saturday); no <c>toAssigneeId</c> keeps the slot's person.</summary>
public sealed record ApplyPromotionRequest(string? PlanId, string? TaskId, int? WeekIndex, int? Weekday, int? ToWeekday, string? ToAssigneeId);

/// <summary>A dismissed suggestion: the slot, the target and the newest evidence occurrence; newer evidence brings the suggestion back. <c>toAssigneeId</c> is required, null when only the weekday changes.</summary>
public sealed record DismissPromotionRequest(string? PlanId, string? TaskId, int? WeekIndex, int? Weekday, int? ToWeekday, string? ToAssigneeId, string? LastEvidenceId);

/// <summary>The answer to a dismissal.</summary>
public sealed record PromoteDismissedResponse(bool Dismissed);

/// <summary>The suggestions, one per slot at most, so the list is bounded by the slots of the active plan and not paged.</summary>
public sealed record PromoteSuggestionListResponse(IReadOnlyList<PromoteSuggestionResponse> Items);

/// <summary>
/// <c>GET /api/v2/promote-suggestions</c> (requirements 4.6, 8). Open like in the Node server (<c>routes/promote.ts</c> has no guard on the read):
/// no profile needed. Applying and dismissing a suggestion (the two POST routes of Node) need a planner.
/// </summary>
public static class PromoteEndpoints
{
    public const string PromoteTag = "Promote";

    public const string SlotNotFoundCode = "slot_not_found";

    public static IEndpointRouteBuilder MapPromoteEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/api/v2/promote-suggestions", ListAsync)
            .WithName("listPromoteSuggestions")
            .WithTags(PromoteTag)
            .WithSummary("Suggests moving a slot of the active plan to the weekday its occurrences are repeatedly dragged to.")
            .WithDescription("Needs no profile. A slot is suggested when, in each of the last promoteThreshold consecutive cycles (a setting of at least two), its occurrence was moved to the same weekday within the same week, and optionally always to the same other person. Cycles still to come that nobody has touched are ignored. A dismissed suggestion returns only with newer evidence. Without an active plan the list is empty.")
            .Produces<PromoteSuggestionListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        routes.MapPost("/api/v2/promote-suggestions/apply", ApplyAsync)
            .RequirePlanner()
            .WithName("applyPromoteSuggestion")
            .WithTags(PromoteTag)
            .WithSummary("Moves one slot of the active plan to the suggested weekday, and person when given (planners).")
            .WithDescription("Changes one slot of the ACTIVE plan and is validated like a slot save: a hard error (for example the new weekday is unavailable for the assignee) is refused with 422 invalid_plan, carrying errors, issues, warnings and summary, and nothing changes. A plan that is not the active one answers 409 plan_not_active, a slot that is no longer in the plan 404 slot_not_found. The plan change is audited with meta.promotedFrom (the old position), toWeekday and, when given, toAssigneeId. Like a slot save of the active plan it synchronises the upcoming occurrences in the same transaction (audited with the system as source) and answers like PUT /cycle-plans/{id}/slots: the plan, the warnings, the summary and synchronized. Answers 500 settings_missing when the installation has no settings.")
            .Accepts<ApplyPromotionRequest>("application/json")
            .Produces<PlanSlotsSavedResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost("/api/v2/promote-suggestions/dismiss", DismissAsync)
            .RequirePlanner()
            .WithName("dismissPromoteSuggestion")
            .WithTags(PromoteTag)
            .WithSummary("Dismisses a suggestion until there is newer evidence (planners).")
            .WithDescription("Appends the dismissal to the settings (an earlier dismissal of the same slot and target is replaced, so only the newest evidence counts); the change is audited as a settings update. Dismissing what is already stored writes and audits nothing. Answers 500 settings_missing when the installation has no settings.")
            .Accepts<DismissPromotionRequest>("application/json")
            .Produces<PromoteDismissedResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return routes;
    }

    private static async Task<IResult> ApplyAsync(HttpContext http, IPromoteService promote, ILogger<IPromoteService> logger, CancellationToken cancellationToken)
    {
        var body = await PromoteRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (PromoteRequestParser.ParseApply(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        var result = await promote.ApplyAsync(await ActorOf(http), command, cancellationToken);
        return result.Match(
            saved => Results.Ok(PlanSlotsSavedResponse.From(saved)),
            _ => ProblemResults.Problem(StatusCodes.Status404NotFound, SlotNotFoundCode, "The slot no longer exists in the plan"),
            ProblemResults.From,
            CyclePlanEndpoints.InvalidPlanProblem,
            ProblemResults.From,
            error => ProblemResults.From(error, logger),
            ProblemResults.From);
    }

    private static async Task<IResult> DismissAsync(HttpContext http, IPromoteService promote, ILogger<IPromoteService> logger, CancellationToken cancellationToken)
    {
        var body = await PromoteRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (PromoteRequestParser.ParseDismiss(json).TryPickT1(out var invalid, out var promotion))
        {
            return ProblemResults.From(invalid);
        }

        var result = await promote.DismissAsync(await ActorOf(http), promotion, cancellationToken);
        return result.Match(
            _ => Results.Ok(new PromoteDismissedResponse(true)),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger),
            ProblemResults.From);
    }

    /// <summary>The policy guarantees an actor on every write; a missing one is a programming error and becomes a 500.</summary>
    private static async Task<Actor> ActorOf(HttpContext http) =>
        await http.GetActorAsync() ?? throw new InvalidOperationException("A promotion write ran without an actor; the endpoint must require authorization.");

    private static async Task<IResult> ListAsync(IPromoteService promote, ILogger<IPromoteService> logger, CancellationToken cancellationToken)
    {
        var result = await promote.GetSuggestionsAsync(cancellationToken);
        return result.Match(
            suggestions => Results.Ok(new PromoteSuggestionListResponse([.. suggestions.Select(PromoteSuggestionResponse.From)])),
            error => ProblemResults.From(error, logger));
    }
}
