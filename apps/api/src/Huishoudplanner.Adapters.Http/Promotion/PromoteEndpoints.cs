using System.ComponentModel;
using Huishoudplanner.Adapters.Http.Problems;
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

/// <summary>The suggestions, one per slot at most, so the list is bounded by the slots of the active plan and not paged.</summary>
public sealed record PromoteSuggestionListResponse(IReadOnlyList<PromoteSuggestionResponse> Items);

/// <summary>
/// <c>GET /api/v2/promote-suggestions</c> (requirements 4.6, 8). Open like in the Node server (<c>routes/promote.ts</c> has no guard on the read):
/// no profile needed. Applying and dismissing a suggestion (the two POST routes of Node, planners only) are not part of this slice.
/// </summary>
public static class PromoteEndpoints
{
    public const string PromoteTag = "Promote";

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
        return routes;
    }

    private static async Task<IResult> ListAsync(IPromoteService promote, ILogger<IPromoteService> logger, CancellationToken cancellationToken)
    {
        var result = await promote.GetSuggestionsAsync(cancellationToken);
        return result.Match(
            suggestions => Results.Ok(new PromoteSuggestionListResponse([.. suggestions.Select(PromoteSuggestionResponse.From)])),
            error => ProblemResults.From(error, logger));
    }
}
