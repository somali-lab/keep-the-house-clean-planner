using System.Globalization;
using Huishoudplanner.Adapters.Http.Concurrency;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Tasks;

/// <summary>
/// <c>/api/v2/tasks</c> and <c>/api/v2/rooms/{id}/tasks/bulk</c> (requirements 4.2, 8). Reads are open like in the Node server;
/// create, change, delete and the bulk change need a planner (<c>requirePlanner</c> there, <see cref="AuthorizationPolicies.PlannerPolicy"/> here).
/// A task is normally deactivated; the permanent delete cascades into the plans and the badge rules that name it.
/// </summary>
public static class TaskEndpoints
{
    public const string TasksTag = "Tasks";

    private const string Path = "/api/v2/tasks";

    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path, ListAsync)
            .WithName("listTasks")
            .WithTags(TasksTag)
            .WithSummary("Lists the tasks, ordered by name.")
            .WithDescription("Needs no profile. roomId limits the list to one room, active=true or active=false to active or inactive tasks. The list is paged: pass the nextCursor of a page as cursor for the next one (limit 1 to 200, default 50).")
            .Produces<TaskListResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/{id}", GetAsync)
            .WithName("getTask")
            .WithTags(TasksTag)
            .WithSummary("Reads one task.")
            .WithDescription("Needs no profile. The ETag header carries the version of the task; send it as If-Match when you change or delete the task. Answers 404 not_found for an unknown task and 400 validation_error on id for a malformed id.")
            .Produces<TaskResponse>(StatusCodes.Status200OK)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path, CreateAsync)
            .RequirePlanner()
            .WithName("createTask")
            .WithTags(TasksTag)
            .WithSummary("Creates a task (planners).")
            .WithDescription("The task is created active. Without points the server applies the default for the duration (one point per minute, from 1 to 1000). The room must exist and be active, the interval must be one of the household intervals and the default assignee an active person: otherwise 400 validation_error with unknown_room, inactive_room, unknown_interval, unknown_user or inactive_user on the field. The change is audited.")
            .Accepts<CreateTaskRequest>("application/json")
            .Produces<TaskResponse>(StatusCodes.Status201Created)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPatch(Path + "/{id}", UpdateAsync)
            .RequirePlanner()
            .RequireIfMatch()
            .WithName("updateTask")
            .WithTags(TasksTag)
            .WithSummary("Changes a task, or deactivates one with active=false (planners).")
            .WithDescription("A change that changes nothing writes and audits nothing. A change of the default assignee is audited as its own assign entry. points: null resets the points to the default for the duration (a reset that changes nothing is a no-op). Changed references are checked like on create. Answers 404 not_found for an unknown task. Needs If-Match with the ETag of the task you read: another version is 412 precondition_failed (also for a change that would change nothing), and a change that changes nothing keeps the version.")
            .Accepts<UpdateTaskRequest>("application/json")
            .Produces<TaskResponse>(StatusCodes.Status200OK)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapDelete(Path + "/{id}", DeleteAsync)
            .RequirePlanner()
            .RequireIfMatch()
            .WithName("deleteTask")
            .WithTags(TasksTag)
            .WithSummary("Deletes a task for good (planners).")
            .WithDescription("Normally a task is deactivated instead. The delete removes the task's slots from every plan that holds one (each such plan gets an update entry with the removed slots and meta reason task_delete) and the task from the badge rules that name it (a rule left without tasks is deactivated; reason task_deleted), all in one transaction, and records a delete entry that keeps the removed fields. Occurrences, points and history stay: they carry their own snapshot. Answers 404 not_found for an unknown task, which changes nothing, and 400 validation_error on id for a malformed id. Needs If-Match with the ETag of the task you read; another version is 412 precondition_failed and nothing is deleted.")
            .Produces<TaskDeletedResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost("/api/v2/rooms/{id}/tasks/bulk", BulkAsync)
            .RequirePlanner()
            .WithName("bulkUpdateRoomTasks")
            .WithTags(TasksTag)
            .WithSummary("Deactivates or reassigns all active tasks of a room (planners).")
            .WithDescription("op=deactivate deactivates every active task of the room, op=reassign gives them all the default assignee in defaultAssigneeId (an active person, or null for anyone). One audit entry per task that changes; updated counts them. Answers 404 not_found for an unknown room.")
            .Accepts<BulkRoomTasksRequest>("application/json")
            .Produces<BulkRoomTasksResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        ITaskService tasks,
        ILogger<ITaskService> logger,
        CancellationToken cancellationToken,
        string? roomId = null,
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
            if (int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
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
            return ProblemResults.From(new ValidationErrors(errors));
        }

        var result = await tasks.ListAsync(roomId, activeFilter, limitValue, cursor, cancellationToken);
        return result.Match(
            list => Results.Ok(new TaskListResponse([.. list.Items.Select(TaskResponse.From)], list.NextCursor)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> GetAsync(
        string id,
        HttpContext http,
        ITaskService tasks,
        ILogger<ITaskService> logger,
        CancellationToken cancellationToken)
    {
        var result = await tasks.GetAsync(id, cancellationToken);
        return result.Match(
            task =>
            {
                ETags.Set(http.Response, task.Version);
                return Results.Ok(TaskResponse.From(task));
            },
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        ITaskService tasks,
        ILogger<ITaskService> logger,
        CancellationToken cancellationToken)
    {
        var body = await TaskRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (TaskRequestParser.ParseCreate(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        var result = await tasks.CreateAsync(await ActorOf(http), command, cancellationToken);
        return result.Match(
            task =>
            {
                ETags.Set(http.Response, task.Version);
                return Results.Json(TaskResponse.From(task), statusCode: StatusCodes.Status201Created);
            },
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> UpdateAsync(
        string id,
        HttpContext http,
        ITaskService tasks,
        ILogger<ITaskService> logger,
        CancellationToken cancellationToken)
    {
        var body = await TaskRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (TaskRequestParser.ParsePatch(json).TryPickT1(out var invalid, out var patch))
        {
            return ProblemResults.From(invalid);
        }

        var result = await tasks.UpdateAsync(await ActorOf(http), id, patch, cancellationToken, http.GetIfMatch());
        return result.Match(
            task =>
            {
                ETags.Set(http.Response, task.Version);
                return Results.Ok(TaskResponse.From(task));
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
        ITaskService tasks,
        ILogger<ITaskService> logger,
        CancellationToken cancellationToken)
    {
        var result = await tasks.DeleteAsync(await ActorOf(http), id, cancellationToken, http.GetIfMatch());
        return result.Match(
            _ => Results.Ok(new TaskDeletedResponse(true)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger),
            ProblemResults.From);
    }

    private static async Task<IResult> BulkAsync(
        string id,
        HttpContext http,
        ITaskService tasks,
        ILogger<ITaskService> logger,
        CancellationToken cancellationToken)
    {
        var body = await TaskRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (TaskRequestParser.ParseBulk(json).TryPickT1(out var invalid, out var change))
        {
            return ProblemResults.From(invalid);
        }

        var result = await tasks.BulkUpdateRoomAsync(await ActorOf(http), id, change, cancellationToken);
        return result.Match(
            updated => Results.Ok(new BulkRoomTasksResponse(updated)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    /// <summary>The policy guarantees an actor on every write; a missing one is a programming error and becomes a 500.</summary>
    private static async Task<Actor> ActorOf(HttpContext http) =>
        await http.GetActorAsync() ?? throw new InvalidOperationException("A task write ran without an actor; the endpoint must require authorization.");
}
