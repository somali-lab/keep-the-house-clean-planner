using System.Globalization;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Due;

/// <summary>
/// <c>GET /api/v2/due</c> (requirements 4.5, 8). Open like in the Node server (<c>routes/due.ts</c> has no guard): no profile needed.
/// </summary>
public static class DueEndpoints
{
    public const string DueTag = "Due";

    public static IEndpointRouteBuilder MapDueEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/api/v2/due", ListAsync)
            .WithName("listDue")
            .WithTags(DueTag)
            .WithSummary("Lists every active task with its due state, ranked.")
            .WithDescription("Needs no profile. Highest ratio first, then most days since, then task id. A task that is fine is in the list as well (state ok); a client that wants only due and overdue work leaves the ok items out. The list is paged: pass the nextCursor of a page as cursor for the next one (limit 1 to 200, default 50). The summary counts the whole list. A task whose interval no longer exists is left out.")
            .Produces<DueListResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return routes;
    }

    private static async Task<IResult> ListAsync(
        IDueService due,
        ILogger<IDueService> logger,
        CancellationToken cancellationToken,
        string? limit = null,
        string? cursor = null)
    {
        // Bound as strings so that a malformed value is a field-keyed validation_error, not a framework binding failure.
        int? limitValue = null;
        if (limit is not null)
        {
            if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return ProblemResults.From(ValidationErrors.For("limit", "Must be an integer."));
            }

            limitValue = parsed;
        }

        var result = await due.GetDueAsync(limitValue, cursor, cancellationToken);
        return result.Match(
            list => Results.Ok(new DueListResponse(
                list.Today,
                [.. list.Items.Select(DueItemResponse.From)],
                list.NextCursor,
                new DueSummaryResponse(list.Summary.Due, list.Summary.Overdue))),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }
}
