using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Occurrences;

/// <summary>
/// The extra executions, one-off tasks and the retract of recorded work (requirements 4.4, 8; ADR-0009). Every one needs an actor
/// (<see cref="AuthorizationPolicies.ActorPolicy"/>), like the Node routes. Node's <c>POST /occurrences</c> is <c>POST /api/v2/occurrences</c>,
/// <c>POST /occurrences/one-off</c> is <c>POST /api/v2/occurrences/one-off</c> and <c>POST /occurrences/:id/retract</c> is
/// <c>POST /api/v2/occurrences/{id}/retraction</c>. The <c>requestId</c> idempotency key keeps its replay semantics: <c>201</c> for a new record,
/// <c>200</c> with the stored one for the same request again, <c>409 idempotency_key_conflict</c> for the same key with another request.
/// </summary>
public static class AdhocEndpoints
{
    private const string Path = "/api/v2/occurrences";

    public static IEndpointRouteBuilder MapAdhocEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost(Path, CreateExtraAsync)
            .RequireActor()
            .WithName("createExtraExecution")
            .WithTags(OccurrenceEndpoints.OccurrencesTag)
            .WithSummary("Plans an extra execution of a task, or records it as done today.")
            .WithDescription("Body { taskId, date, assigneeId?, done?, requestId? }. The task must exist and be active (400 validation_error unknown_task, inactive_task) and the day must lie in a generated cycle (409 cycle_not_generated with the date). Several executions of one task on one day coexist; when the task already has an open occurrence that day the answer carries the warning task_already_planned. assigneeId left out means the task's default assignee (the actor when done), null means anyone; an unknown or inactive person is 400 unknown_user or inactive_user. done true is only allowed for today (400 done_requires_today) and for a person (400 done_requires_person) and refreshes the task's lastCompletedAt. requestId makes the request idempotent: the same key for the same request (task, day, done) answers 200 with the stored record and writes and audits nothing, the same key for another request answers 409 idempotency_key_conflict; of concurrent requests with one key exactly one creates (201). Audited as one create entry with meta origin adhoc, kind extra, recordedDone and requestId.")
            .Accepts<CreateExtraExecutionRequest>("application/json")
            .Produces<OccurrenceResponse>(StatusCodes.Status201Created)
            .Produces<OccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/one-off", CreateOneOffAsync)
            .RequireActor()
            .WithName("createOneOffOccurrence")
            .WithTags(OccurrenceEndpoints.OccurrencesTag)
            .WithSummary("Plans a one-off task, or records it as done today.")
            .WithDescription("Body { name, roomId?, durationMinutes, date, assigneeId?, done?, points?, requestId? }. A one-off task has no task record (taskId null): its name (trimmed, 1 to 120 characters), duration and room live on the occurrence, so it never appears in the task list, the due list, the planner or the AI input. An unknown or inactive room is 400 unknown_room or inactive_room; no room is stored as null. assigneeId left out means nobody (the actor when done), null means anyone. points (0 to 1000) left out means one point per minute of the duration. The rules for done, the day, the idempotency key (a repeat matches on name, day and done) and the audit are those of POST /api/v2/occurrences, with meta kind one_off. A one-off task never warns task_already_planned.")
            .Accepts<CreateOneOffRequest>("application/json")
            .Produces<OccurrenceResponse>(StatusCodes.Status201Created)
            .Produces<OccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/{id}/retraction", RetractAsync)
            .RequireActor()
            .WithName("retractOccurrence")
            .WithTags(OccurrenceEndpoints.OccurrencesTag)
            .WithSummary("Undoes recorded extra work of today.")
            .WithDescription("Recorded work (an extra execution or one-off task created done) has no planned state to return to, so undoing it deletes it, audited as a delete with meta reason retract that keeps the removed fields; the task's lastCompletedAt falls back to the newest remaining completion. Only work of today in the household timezone (409 retract_not_today); anything else, planned occurrences included, is 409 not_retractable. A second retract answers 404. Uncomplete of recorded work answers 409 retract_required.")
            .Produces<RetractOccurrenceResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> CreateExtraAsync(
        HttpContext http,
        IAdhocOccurrenceService adhoc,
        ILogger<IAdhocOccurrenceService> logger,
        CancellationToken cancellationToken)
    {
        var body = await OccurrenceRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (OccurrenceRequestParser.ParseExtra(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        return Created(await adhoc.CreateExtraAsync(await ActorOf(http), command, cancellationToken), logger);
    }

    private static async Task<IResult> CreateOneOffAsync(
        HttpContext http,
        IAdhocOccurrenceService adhoc,
        ILogger<IAdhocOccurrenceService> logger,
        CancellationToken cancellationToken)
    {
        var body = await OccurrenceRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (OccurrenceRequestParser.ParseOneOff(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        return Created(await adhoc.CreateOneOffAsync(await ActorOf(http), command, cancellationToken), logger);
    }

    private static async Task<IResult> RetractAsync(
        string id,
        HttpContext http,
        IAdhocOccurrenceService adhoc,
        ILogger<IAdhocOccurrenceService> logger,
        CancellationToken cancellationToken)
    {
        var result = await adhoc.RetractAsync(await ActorOf(http), id, cancellationToken);
        return result.Match(
            _ => Results.Ok(new RetractOccurrenceResponse(true, id.ToLowerInvariant())),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    /// <summary>201 for a new record, 200 when a repeated request key replayed the stored one.</summary>
    private static IResult Created(OneOf<AdhocResult, ValidationErrors, ConflictError, SettingsMissing, PortError> result, ILogger logger) =>
        result.Match(
            created => Results.Json(
                OccurrenceResponse.From(created.View, created.Warnings),
                statusCode: created.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));

    /// <summary>The policy guarantees an actor on every write; a missing one is a programming error and becomes a 500.</summary>
    private static async Task<Actor> ActorOf(HttpContext http) =>
        await http.GetActorAsync() ?? throw new InvalidOperationException("An ad-hoc occurrence write ran without an actor; the endpoint must require authorization.");
}
