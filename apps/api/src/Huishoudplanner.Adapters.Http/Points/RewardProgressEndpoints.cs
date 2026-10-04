using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Rewards;
using Microsoft.Extensions.Logging;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Points;

/// <summary>
/// <c>GET /api/v2/points/progress</c> (requirements 4.12): the reward meter of one person. Open like the other points reads (no profile needed in the
/// Node server either). The query arrives as strings, so a malformed value is a field-keyed <c>400 validation_error</c>.
/// </summary>
public static class RewardProgressEndpoints
{
    public static IEndpointRouteBuilder MapRewardProgressEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/api/v2/points/progress", ProgressAsync)
            .WithName("getPointsProgress")
            .WithTags(PointsEndpoints.PointsTag)
            .WithSummary("How far one person is towards the goal of this week or cycle.")
            .WithDescription("Needs no profile. personId and period (week or cycle) are required. The period is the one of today in the household timezone. earnedPoints are the executions and bonuses dated in it, so a redemption never lowers them; the goal is the explicit goal of the settings (0 means none), else the points of the work planned for the person as its owner (null when nothing is planned). percent is capped at 100. eggs (one per full 10%) and eggCount are the reward meter; money is in cents and null while a point is worth nothing.")
            .Produces<RewardProgressResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ProgressAsync(
        IRewardProgressService progress,
        ILogger<IRewardProgressService> logger,
        CancellationToken cancellationToken,
        string? personId = null,
        string? period = null)
    {
        if (Parse(personId, period).TryPickT1(out var invalid, out var request))
        {
            return ProblemResults.From(invalid);
        }

        var result = await progress.ProgressAsync(request, cancellationToken);
        return result.Match(
            done => Results.Ok(RewardProgressResponse.From(done)),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static OneOf<RewardProgressRequest, ValidationErrors> Parse(string? personId, string? period)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (personId is null)
        {
            errors["personId"] = ["is required"];
        }

        PeriodUnit? unit = null;
        if (period is null)
        {
            errors["period"] = ["is required"];
        }
        else if (string.Equals(period, "week", StringComparison.Ordinal))
        {
            unit = PeriodUnit.Week;
        }
        else if (string.Equals(period, "cycle", StringComparison.Ordinal))
        {
            unit = PeriodUnit.Cycle;
        }
        else
        {
            errors["period"] = ["Must be week or cycle."];
        }

        return errors.Count > 0 ? new ValidationErrors(errors) : new RewardProgressRequest(personId!, unit!.Value);
    }
}
