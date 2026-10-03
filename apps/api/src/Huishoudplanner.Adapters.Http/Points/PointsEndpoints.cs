using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Points;

/// <summary>
/// <c>/api/v2/points</c> (requirements 4.12, 8): the balances and the entries of the ledger, which need no profile like in the Node server, and
/// the reconciliation, which needs an administrator (<c>requireAdmin</c> there, <see cref="AuthorizationPolicies.AdminPolicy"/> here). The
/// redemptions (slice 4.3) and the progress of the reward meter (slice 4.4) join the same path.
/// </summary>
public static class PointsEndpoints
{
    public const string PointsTag = "Points";

    private const string Path = "/api/v2/points";

    public static IEndpointRouteBuilder MapPointsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path + "/balances", BalancesAsync)
            .WithName("getPointsBalances")
            .WithTags(PointsTag)
            .WithSummary("The balance of every person over a range.")
            .WithDescription("Needs no profile. from and to are optional day keys (YYYY-MM-DD), both included; without them the balances cover the whole ledger (from_after_to when from is after to). Lists every active person, also at 0, and every inactive person with entries in the range, in the order of the user list. points is the balance (earned minus redeemed), money is in cents at the factor in force now and null while a point is worth nothing.")
            .Produces<PointsBalancesResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/entries", EntriesAsync)
            .WithName("listPointEntries")
            .WithTags(PointsTag)
            .WithSummary("The ledger entries of one person in a range, newest date first.")
            .WithDescription("Needs no profile. personId, from and to are required; the range is at most 371 days, both days included (range_too_large on to) and must not run backwards (from_after_to on from). The list is paged: pass the nextCursor of a page as cursor for the next one (limit 1 to 500, default 100); nextCursor is null on the last page.")
            .Produces<PointEntriesResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/recompute", RecomputeAsync)
            .RequireAdmin()
            .WithName("recomputePoints")
            .WithTags(PointsTag)
            .WithSummary("Reconciles the ledger with the occurrences now (administrators).")
            .WithDescription("Takes no body. Makes every execution entry match its occurrence (it inserts what is missing, updates what differs and deletes what has no occurrence) after migrating the fields of an installation from before points. A run that changes something records one summary entry in the history and nothing per entry; a run that changes nothing writes and audits nothing. Answers what it did.")
            .Produces<PointsRecomputeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> BalancesAsync(
        IPointsService points,
        ILogger<IPointsService> logger,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null)
    {
        if (PointsRequestParser.ParseBalances(from, to).TryPickT1(out var invalid, out var range))
        {
            return ProblemResults.From(invalid);
        }

        var result = await points.BalancesAsync(range.From, range.To, cancellationToken);
        return result.Match(
            balances => Results.Ok(PointsBalancesResponse.FromDomain(balances)),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> EntriesAsync(
        IPointsService points,
        ILogger<IPointsService> logger,
        CancellationToken cancellationToken,
        string? personId = null,
        string? from = null,
        string? to = null,
        string? limit = null,
        string? cursor = null)
    {
        if (PointsRequestParser.ParseEntries(personId, from, to, limit, cursor).TryPickT1(out var invalid, out var request))
        {
            return ProblemResults.From(invalid);
        }

        var result = await points.EntriesAsync(request, cancellationToken);
        return result.Match(
            list => Results.Ok(new PointEntriesResponse([.. list.Items.Select(PointEntryResponse.From)], list.NextCursor)),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> RecomputeAsync(
        HttpContext http,
        IPointsService points,
        ILogger<IPointsService> logger,
        CancellationToken cancellationToken)
    {
        // The policy guarantees an administrator; a missing actor is a programming error and becomes a 500.
        var actor = await http.GetActorAsync() ?? throw new InvalidOperationException("The recompute ran without an actor; the endpoint must require an administrator.");
        var result = await points.RecomputeAsync(AuditActor.From(actor), PointsRecomputeTrigger.Admin, cancellationToken);
        return result.Match(
            done => Results.Ok(PointsRecomputeResponse.From(done)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }
}
