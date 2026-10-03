using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Occurrences;

/// <summary>
/// <c>/api/v2/occurrences</c> (requirements 4.4, 4.8, 8). Reads are open like in the Node server; every action needs an actor
/// (<see cref="AuthorizationPolicies.ActorPolicy"/>), the correction of a completion and the permanent delete an administrator. The Node
/// <c>PATCH /occurrences/:id</c> with an <c>action</c> is one endpoint per intent here: <c>complete</c> -> <c>POST .../complete</c>,
/// <c>uncomplete</c> -> <c>POST .../uncomplete</c>, <c>edit_completion</c> -> <c>POST .../completion</c>, <c>skip</c> -> <c>POST .../skip</c>,
/// <c>reschedule</c> -> <c>POST .../reschedule</c>, <c>assign</c> -> <c>POST .../assignment</c>; the claim stays <c>POST .../claim</c>.
/// </summary>
public static class OccurrenceEndpoints
{
    public const string OccurrencesTag = "Occurrences";

    private const string Path = "/api/v2/occurrences";

    public static IEndpointRouteBuilder MapOccurrenceEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path, ListAsync)
            .WithName("listOccurrences")
            .WithTags(OccurrencesTag)
            .WithSummary("Lists the occurrences on the days from to to, in display order.")
            .WithDescription("Needs no profile. from and to are required day keys (YYYY-MM-DD), both included, and from must not be after to (from_after_to). assigneeId and status (open, done, skipped) filter the list. Every occurrence carries isOverdue, movedFrom and where its day falls in the cycles (cycleIndex, weekIndex). The list is paged: pass the nextCursor of a page as cursor for the next one (limit 1 to 500, default 100).")
            .Produces<OccurrenceListResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/{id}", GetAsync)
            .WithName("getOccurrence")
            .WithTags(OccurrencesTag)
            .WithSummary("Returns one occurrence.")
            .WithDescription("Needs no profile. Answers 404 not_found for an unknown occurrence.")
            .Produces<OccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/{id}/complete", CompleteAsync)
            .RequireActor()
            .WithName("completeOccurrence")
            .WithTags(OccurrencesTag)
            .WithSummary("Completes an open or skipped occurrence.")
            .WithDescription("Work of someone else needs a choice (ADR-0011): completedBy names the person credited (an active person) or takeOver true makes the actor the one who did it and the assignee. Without either: 400 validation_error with completion_choice_required on completedBy; both: completion_choice_conflict; an unknown or inactive person: unknown_user or inactive_user. Unassigned work and work of the actor default to the actor and a completion claims unassigned work. A done occurrence answers 409 invalid_transition. The task's lastCompletedAt follows, audited as its own entry. The body may be left out.")
            .Accepts<CompleteOccurrenceRequest>("application/json")
            .Produces<OccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/{id}/uncomplete", UncompleteAsync)
            .RequireActor()
            .WithName("uncompleteOccurrence")
            .WithTags(OccurrencesTag)
            .WithSummary("Undoes a completion: back to the status before it.")
            .WithDescription("Only a done occurrence (409 invalid_transition otherwise). Recorded work has no planned state to return to: 409 retract_required. The points snapshot is cleared and the task's lastCompletedAt falls back to the newest remaining completion.")
            .Produces<OccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/{id}/completion", EditCompletionAsync)
            .RequireAdmin()
            .WithName("editOccurrenceCompletion")
            .WithTags(OccurrencesTag)
            .WithSummary("Corrects a recorded completion (administrators).")
            .WithDescription("Takes date, completedAt and completedBy. A done occurrence only (409 invalid_transition), an existing person (400 unknown_user on completedBy) and, when date differs from the occurrence's day, a generated cycle for that day (409 cycle_not_generated with the date). Audited as an update with the correction reason completion; the task's lastCompletedAt follows.")
            .Accepts<EditCompletionRequest>("application/json")
            .Produces<OccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/{id}/skip", SkipAsync)
            .RequireActor()
            .WithName("skipOccurrence")
            .WithTags(OccurrencesTag)
            .WithSummary("Skips an open occurrence.")
            .WithDescription("An optional reason of at most 500 characters. Only an open occurrence (409 invalid_transition otherwise). A skipped occurrence does not roll over. The body may be left out.")
            .Accepts<SkipOccurrenceRequest>("application/json")
            .Produces<OccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/{id}/reschedule", RescheduleAsync)
            .RequireActor()
            .WithName("rescheduleOccurrence")
            .WithTags(OccurrencesTag)
            .WithSummary("Moves an open occurrence to another day.")
            .WithDescription("The planned day stays, the cycle follows the new day. A day outside the generated cycles: 409 cycle_not_generated; a status other than open: 409 invalid_transition. A move to the same day writes and audits nothing. Moving onto a weekday the assignee is unavailable on is allowed and answers the warning assignee_unavailable.")
            .Accepts<RescheduleOccurrenceRequest>("application/json")
            .Produces<OccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/{id}/assignment", AssignAsync)
            .RequireActor()
            .WithName("assignOccurrence")
            .WithTags(OccurrencesTag)
            .WithSummary("Assigns an open occurrence to a person, or to anyone.")
            .WithDescription("assigneeId is required: an active person (400 unknown_user or inactive_user otherwise), or null for anyone. Only an open occurrence (409 invalid_transition). Assigning the person who already has it writes nothing. Answers the warning assignee_unavailable when the person is unavailable on the occurrence's weekday.")
            .Accepts<AssignOccurrenceRequest>("application/json")
            .Produces<OccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/{id}/claim", ClaimAsync)
            .RequireActor()
            .WithName("claimOccurrence")
            .WithTags(OccurrencesTag)
            .WithSummary("The actor takes an unassigned open occurrence.")
            .WithDescription("Atomic: of two claims exactly one wins. A done or skipped occurrence answers 409 invalid_transition, one that already has an assignee 409 already_claimed. Audited as an assign entry with meta.claim.")
            .Produces<OccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapDelete(Path + "/{id}", DeleteAsync)
            .RequireAdmin()
            .WithName("deleteOccurrence")
            .WithTags(OccurrencesTag)
            .WithSummary("Permanently deletes a completed occurrence (administrators).")
            .WithDescription("An administrator's correction: only a done occurrence (409 invalid_transition otherwise). Audited as a delete with the correction reason completion; the task's lastCompletedAt falls back to the newest remaining completion.")
            .Produces<DeleteOccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        IOccurrenceService occurrences,
        ILogger<IOccurrenceService> logger,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null,
        string? assigneeId = null,
        string? status = null,
        string? limit = null,
        string? cursor = null)
    {
        if (OccurrenceRequestParser.ParseList(from, to, assigneeId, status, limit, cursor).TryPickT1(out var invalid, out var request))
        {
            return ProblemResults.From(invalid);
        }

        var result = await occurrences.ListAsync(request, cancellationToken);
        return result.Match(
            list => Results.Ok(new OccurrenceListResponse([.. list.Items.Select(view => OccurrenceResponse.From(view))], list.NextCursor)),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> GetAsync(
        string id,
        IOccurrenceService occurrences,
        ILogger<IOccurrenceService> logger,
        CancellationToken cancellationToken)
    {
        var result = await occurrences.GetAsync(id, cancellationToken);
        return result.Match(
            view => Results.Ok(OccurrenceResponse.From(view)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> CompleteAsync(
        string id,
        HttpContext http,
        IOccurrenceService occurrences,
        ILogger<IOccurrenceService> logger,
        CancellationToken cancellationToken)
    {
        var body = await OccurrenceRequestParser.ReadOptionalBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (OccurrenceRequestParser.ParseComplete(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        return View(await occurrences.CompleteAsync(await ActorOf(http), id, command, cancellationToken), logger);
    }

    private static async Task<IResult> UncompleteAsync(
        string id,
        HttpContext http,
        IOccurrenceService occurrences,
        ILogger<IOccurrenceService> logger,
        CancellationToken cancellationToken) =>
        View(await occurrences.UncompleteAsync(await ActorOf(http), id, cancellationToken), logger);

    private static async Task<IResult> EditCompletionAsync(
        string id,
        HttpContext http,
        IOccurrenceService occurrences,
        ILogger<IOccurrenceService> logger,
        CancellationToken cancellationToken)
    {
        var body = await OccurrenceRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (OccurrenceRequestParser.ParseEditCompletion(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        return View(await occurrences.EditCompletionAsync(await ActorOf(http), id, command, cancellationToken), logger);
    }

    private static async Task<IResult> SkipAsync(
        string id,
        HttpContext http,
        IOccurrenceService occurrences,
        ILogger<IOccurrenceService> logger,
        CancellationToken cancellationToken)
    {
        var body = await OccurrenceRequestParser.ReadOptionalBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (OccurrenceRequestParser.ParseSkip(json).TryPickT1(out var invalid, out var reason))
        {
            return ProblemResults.From(invalid);
        }

        return View(await occurrences.SkipAsync(await ActorOf(http), id, reason, cancellationToken), logger);
    }

    private static async Task<IResult> RescheduleAsync(
        string id,
        HttpContext http,
        IOccurrenceService occurrences,
        ILogger<IOccurrenceService> logger,
        CancellationToken cancellationToken)
    {
        var body = await OccurrenceRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (OccurrenceRequestParser.ParseReschedule(json).TryPickT1(out var invalid, out var day))
        {
            return ProblemResults.From(invalid);
        }

        return Change(await occurrences.RescheduleAsync(await ActorOf(http), id, day, cancellationToken), logger);
    }

    private static async Task<IResult> AssignAsync(
        string id,
        HttpContext http,
        IOccurrenceService occurrences,
        ILogger<IOccurrenceService> logger,
        CancellationToken cancellationToken)
    {
        var body = await OccurrenceRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (OccurrenceRequestParser.ParseAssign(json).TryPickT1(out var invalid, out var assigneeId))
        {
            return ProblemResults.From(invalid);
        }

        return Change(await occurrences.AssignAsync(await ActorOf(http), id, assigneeId, cancellationToken), logger);
    }

    private static async Task<IResult> ClaimAsync(
        string id,
        HttpContext http,
        IOccurrenceService occurrences,
        ILogger<IOccurrenceService> logger,
        CancellationToken cancellationToken) =>
        View(await occurrences.ClaimAsync(await ActorOf(http), id, cancellationToken), logger);

    private static async Task<IResult> DeleteAsync(
        string id,
        HttpContext http,
        IOccurrenceService occurrences,
        ILogger<IOccurrenceService> logger,
        CancellationToken cancellationToken)
    {
        var result = await occurrences.DeleteCompletedAsync(await ActorOf(http), id, cancellationToken);
        return result.Match(
            _ => Results.Ok(new DeleteOccurrenceResponse(true)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static IResult View(OneOf<OccurrenceView, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError> result, ILogger logger) =>
        result.Match(
            view => Results.Ok(OccurrenceResponse.From(view)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));

    private static IResult Change(OneOf<OccurrenceChange, NotFound, ValidationErrors, ConflictError, SettingsMissing, PortError> result, ILogger logger) =>
        result.Match(
            change => Results.Ok(OccurrenceResponse.From(change.View, change.Warnings)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));

    /// <summary>The policy guarantees an actor on every write; a missing one is a programming error and becomes a 500.</summary>
    private static async Task<Actor> ActorOf(HttpContext http) =>
        await http.GetActorAsync() ?? throw new InvalidOperationException("An occurrence write ran without an actor; the endpoint must require authorization.");
}
