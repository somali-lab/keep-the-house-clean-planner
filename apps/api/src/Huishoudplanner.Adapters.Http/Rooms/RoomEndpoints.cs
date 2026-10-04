using Huishoudplanner.Adapters.Http.Concurrency;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.OpenApi;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Rooms;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Rooms;

/// <summary>
/// <c>/api/v2/rooms</c> (requirements 4.1, 8). Reads are open like in the Node server; create, change and delete need an
/// administrator (<c>requireAdmin</c> there, <see cref="AuthorizationPolicies.AdminPolicy"/> here).
/// </summary>
public static class RoomEndpoints
{
    private const string Path = "/api/v2/rooms";

    public static IEndpointRouteBuilder MapRoomEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path, ListAsync)
            .WithName("listRooms")
            .WithTags(OpenApiSetup.RoomsTag)
            .WithSummary("Lists the rooms in house order.")
            .WithDescription("Rooms are ordered by sortOrder, then name. active=true or active=false limits the list to active or inactive rooms. The list is paged: pass the nextCursor of a page as cursor for the next one (limit 1 to 200, default 50).")
            .Produces<RoomListResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/{id}", GetAsync)
            .WithName("getRoom")
            .WithTags(OpenApiSetup.RoomsTag)
            .WithSummary("Reads one room.")
            .WithDescription("The ETag header carries the version of the room; send it as If-Match when you change or delete the room. Answers 404 not_found for an unknown room and 400 validation_error on id for a malformed id.")
            .Produces<RoomResponse>(StatusCodes.Status200OK)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path, CreateAsync)
            .RequireAdmin()
            .WithName("createRoom")
            .WithTags(OpenApiSetup.RoomsTag)
            .WithSummary("Creates a room (administrators).")
            .WithDescription("The room is created active. Without sortOrder it goes ten places after the last room (10 for the first one). The change is audited.")
            .Accepts<CreateRoomRequest>("application/json")
            .Produces<RoomResponse>(StatusCodes.Status201Created)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPatch(Path + "/{id}", UpdateAsync)
            .RequireAdmin()
            .RequireIfMatch()
            .WithName("updateRoom")
            .WithTags(OpenApiSetup.RoomsTag)
            .WithSummary("Changes a room (administrators).")
            .WithDescription("Renames, reorders, deactivates or marks the room virtual. A change that changes nothing writes and audits nothing. Answers 404 not_found for an unknown room. Needs If-Match with the ETag of the room you read: another version is 412 precondition_failed (also for a change that would change nothing), and a change that changes nothing keeps the version.")
            .Accepts<UpdateRoomRequest>("application/json")
            .Produces<RoomResponse>(StatusCodes.Status200OK)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapDelete(Path + "/{id}", DeleteAsync)
            .RequireAdmin()
            .RequireIfMatch()
            .WithName("deleteRoom")
            .WithTags(OpenApiSetup.RoomsTag)
            .WithSummary("Deletes a room that holds no tasks (administrators).")
            .WithDescription("A room that still holds tasks, active or inactive, cannot be deleted: 409 room_in_use with the number of tasks in the taskCount extension. Answers 404 not_found for an unknown room. Needs If-Match with the ETag of the room you read; another version is 412 precondition_failed and nothing is deleted.")
            .Produces<RoomDeletedResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        IRoomService rooms,
        ILogger<IRoomService> logger,
        CancellationToken cancellationToken,
        string? active = null,
        string? limit = null,
        string? cursor = null)
    {
        // Bound as strings so that a malformed value is a field-keyed validation_error, not a framework binding failure.
        var errors = new Dictionary<string, string[]>();
        bool? activeFilter = null;
        if (active is not null)
        {
            if (active is "true" or "false")
            {
                activeFilter = active == "true";
            }
            else
            {
                errors["active"] = ["Must be 'true' or 'false'."];
            }
        }

        int? limitValue = null;
        if (limit is not null)
        {
            if (int.TryParse(limit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                limitValue = parsed;
            }
            else
            {
                errors["limit"] = ["Must be an integer."];
            }
        }

        if (errors.Count > 0)
        {
            return ProblemResults.From(new Huishoudplanner.Domain.Errors.ValidationErrors(errors));
        }

        var result = await rooms.ListAsync(activeFilter, limitValue, cursor, cancellationToken);
        return result.Match(
            list => Results.Ok(new RoomListResponse([.. list.Items.Select(RoomResponse.From)], list.NextCursor)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> GetAsync(
        string id,
        HttpContext http,
        IRoomService rooms,
        ILogger<IRoomService> logger,
        CancellationToken cancellationToken)
    {
        var result = await rooms.GetAsync(id, cancellationToken);
        return result.Match(
            room =>
            {
                ETags.Set(http.Response, room.Version);
                return Results.Ok(RoomResponse.From(room));
            },
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        IRoomService rooms,
        ILogger<IRoomService> logger,
        CancellationToken cancellationToken)
    {
        var body = await RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (RoomRequestParser.ParseCreate(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        var result = await rooms.CreateAsync(await ActorOf(http), command, cancellationToken);
        return result.Match(
            room =>
            {
                ETags.Set(http.Response, room.Version);
                return Results.Json(RoomResponse.From(room), statusCode: StatusCodes.Status201Created);
            },
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> UpdateAsync(
        string id,
        HttpContext http,
        IRoomService rooms,
        ILogger<IRoomService> logger,
        CancellationToken cancellationToken)
    {
        var body = await RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (RoomRequestParser.ParsePatch(json).TryPickT1(out var invalid, out var patch))
        {
            return ProblemResults.From(invalid);
        }

        var result = await rooms.UpdateAsync(await ActorOf(http), id, patch, cancellationToken, http.GetIfMatch());
        return result.Match(
            room =>
            {
                ETags.Set(http.Response, room.Version);
                return Results.Ok(RoomResponse.From(room));
            },
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger),
            ProblemResults.From);
    }

    private static async Task<IResult> DeleteAsync(
        string id,
        HttpContext http,
        IRoomService rooms,
        ILogger<IRoomService> logger,
        CancellationToken cancellationToken)
    {
        var result = await rooms.DeleteAsync(await ActorOf(http), id, cancellationToken, http.GetIfMatch());
        return result.Match(
            _ => Results.Ok(new RoomDeletedResponse(true)),
            ProblemResults.From,
            ProblemResults.From,
            RoomInUseProblem,
            ProblemResults.From,
            error => ProblemResults.From(error, logger),
            ProblemResults.From);
    }

    /// <summary><c>409 room_in_use</c>; the number of tasks travels as the <c>taskCount</c> extension (Node: <c>details.taskCount</c>).</summary>
    private static IResult RoomInUseProblem(RoomInUse inUse) =>
        Results.Problem(new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Type = ProblemTypes.UrnFor("room_in_use"),
            Detail = "Remove all tasks from this room before deleting it",
            Extensions = { ["taskCount"] = inUse.TaskCount },
        });

    /// <summary>The policy guarantees an actor on every write; a missing one is a programming error and becomes a 500.</summary>
    private static async Task<Actor> ActorOf(HttpContext http) =>
        await http.GetActorAsync() ?? throw new InvalidOperationException("A room write ran without an actor; the endpoint must require authorization.");
}
