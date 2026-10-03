using System.Globalization;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Cycles;

/// <summary>
/// <c>GET /api/v2/cycles</c> (requirements 3 and 8): the read-only list of generated cycles that the export dialog uses to know which weeks
/// exist. Open like in the Node server; cycles are created by generation only, so there is no write endpoint.
/// </summary>
public static class CycleEndpoints
{
    public const string CyclesTag = "Cycles";

    private const string Path = "/api/v2/cycles";

    public static IEndpointRouteBuilder MapCycleEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path, ListAsync)
            .WithName("listCycles")
            .WithTags(CyclesTag)
            .WithSummary("Lists the generated cycles in index order.")
            .WithDescription("Needs no profile. A cycle is 28 days from a Monday; index 0 starts on the anchor date and indexes before it are negative. Days in a cycle that is not listed are not generated yet. The list is paged: pass the nextCursor of a page as cursor for the next one (limit 1 to 200, default 50).")
            .Produces<CycleListResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        ICycleService cycles,
        ILogger<ICycleService> logger,
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

        var result = await cycles.ListAsync(limitValue, cursor, cancellationToken);
        return result.Match(
            list => Results.Ok(new CycleListResponse([.. list.Items.Select(CycleResponse.From)], list.NextCursor)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }
}
