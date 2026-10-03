using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Points;

/// <summary>
/// The redemptions of the points ledger (requirements 4.12, 8; ADR-0011). The Node routes <c>POST /points/redemptions</c>,
/// <c>DELETE /points/redemptions/:id</c> and <c>GET /points/redemptions/count</c> are <c>POST /api/v2/points/redemptions</c>,
/// <c>DELETE /api/v2/points/redemptions/{id}</c> and <c>GET /api/v2/points/redemptions/count</c>; the redemptions are listed with the other ledger
/// entries (<c>GET /api/v2/points/entries</c>, Node has no list of its own). A booking and an undo need an actor
/// (<see cref="AuthorizationPolicies.ActorPolicy"/>, <c>requireActor</c> there), the count needs none.
/// </summary>
public static class RedemptionEndpoints
{
    private const string Path = "/api/v2/points/redemptions";

    public static IEndpointRouteBuilder MapRedemptionEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path + "/count", CountAsync)
            .WithName("countRedemptions")
            .WithTags(PointsEndpoints.PointsTag)
            .WithSummary("How many redemptions exist.")
            .WithDescription("Needs no profile. The import screen warns that importing an older file removes the redemptions.")
            .Produces<RedemptionCountResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path, BookAsync)
            .RequireActor()
            .WithName("bookRedemption")
            .WithTags(PointsEndpoints.PointsTag)
            .WithSummary("Books a redemption: a person gives up points.")
            .WithDescription("Body { personId?, points, note?, requestId? }. personId defaults to the active profile; only an administrator may book for someone else (403 permission_denied), and the person must be active (400 validation_error unknown_user, inactive_user). points is a whole number of at least 1 and at most the balance over the whole ledger: a larger booking is 409 insufficient_balance with the balance and the requested points. note is trimmed, at most 200 characters, empty is none. The booking is a negative ledger entry dated today in the household timezone that keeps the factor and currency in force now; the reconciliation never touches it. Two bookings of one person at once never overdraw the balance: the check and the insert are one transaction and the bookings of one person conflict on a guard document, so the later one reads the earlier one's entry (a booking that keeps losing answers 409 write_conflict; retry it). requestId makes the request idempotent: the same key for the same person, points and note answers 200 with the stored booking and writes and audits nothing, the same key for another request answers 409 idempotency_key_conflict; of concurrent requests with one key exactly one creates (201). Audited as one points create entry with meta reason redemption; the request key is not part of it. Answers the ledger entry without its request key.")
            .Accepts<CreateRedemptionRequest>("application/json")
            .Produces<PointEntryResponse>(StatusCodes.Status201Created)
            .Produces<PointEntryResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapDelete(Path + "/{id}", UndoAsync)
            .RequireActor()
            .WithName("undoRedemption")
            .WithTags(PointsEndpoints.PointsTag)
            .WithSummary("Takes a redemption back.")
            .WithDescription("The person the redemption belongs to can undo it on the day it was booked (household timezone); an administrator at any time. Later the owner gets 403 redemption_locked, anybody else 403 permission_denied. An unknown id and an entry that is not a redemption are 404; a second undo is 404 too and writes nothing. Audited as one points delete entry with meta reason redemption_undone that keeps the removed fields.")
            .Produces<DeleteRedemptionResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> CountAsync(IRedemptionService redemptions, ILogger<IRedemptionService> logger, CancellationToken cancellationToken)
    {
        var result = await redemptions.CountAsync(cancellationToken);
        return result.Match(count => Results.Ok(new RedemptionCountResponse(count)), error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> BookAsync(
        HttpContext http,
        IRedemptionService redemptions,
        ILogger<IRedemptionService> logger,
        CancellationToken cancellationToken)
    {
        var body = await RedemptionRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (RedemptionRequestParser.Parse(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        var result = await redemptions.BookAsync(await ActorOf(http), command, cancellationToken);
        return result.Match(
            booked => Results.Json(
                PointEntryResponse.From(booked.View),
                statusCode: booked.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> UndoAsync(
        string id,
        HttpContext http,
        IRedemptionService redemptions,
        ILogger<IRedemptionService> logger,
        CancellationToken cancellationToken)
    {
        var result = await redemptions.UndoAsync(await ActorOf(http), id, cancellationToken);
        return result.Match(
            _ => Results.Ok(new DeleteRedemptionResponse(true)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    /// <summary>The policy guarantees an actor on every write; a missing one is a programming error and becomes a 500.</summary>
    private static async Task<Actor> ActorOf(HttpContext http) =>
        await http.GetActorAsync() ?? throw new InvalidOperationException("A redemption write ran without an actor; the endpoint must require authorization.");
}
