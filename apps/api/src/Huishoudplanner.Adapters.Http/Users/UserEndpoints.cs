using System.Globalization;
using Huishoudplanner.Adapters.Http.Concurrency;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Users;

/// <summary>
/// <c>/api/v2/users</c> (requirements sections 2, 3 and 8). Endpoints parse, authorize and map results; the rules live in
/// <see cref="IUserService"/>. Reads need no profile; creating and changing a person need an administrator; a person
/// sets their own browser notification moments (an administrator anyone's).
/// </summary>
public static class UserEndpoints
{
    public const string UsersTag = "Users";

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/api/v2/users", ListAsync)
            .WithTags(UsersTag)
            .WithName("listUsers")
            .WithSummary("Lists the people of the household, oldest first.")
            .WithDescription("Needs no profile. Filter with active=true or active=false; page with limit (1 to 500, default 100) and the nextCursor of the previous page.")
            .Produces<UserListResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet("/api/v2/users/{id}", GetAsync)
            .WithTags(UsersTag)
            .WithName("getUser")
            .WithSummary("Reads one person.")
            .WithDescription("Needs no profile. The ETag header carries the version of the person; send it as If-Match when you change the person or set their browser notifications. Answers 404 not_found for an unknown person and 400 validation_error on id for a malformed id.")
            .Produces<UserResponse>(StatusCodes.Status200OK)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost("/api/v2/users", CreateAsync)
            .WithTags(UsersTag)
            .RequireAdmin()
            .WithName("createUser")
            .WithSummary("Creates a person.")
            .WithDescription("Administrators only. Audited as user/create.")
            .Accepts<CreateUserRequest>("application/json")
            .Produces<UserResponse>(StatusCodes.Status201Created)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPatch("/api/v2/users/{id}", UpdateAsync)
            .WithTags(UsersTag)
            .RequireAdmin()
            .RequireIfMatch()
            .Accepts<UpdateUserRequest>("application/json")
            .WithName("updateUser")
            .WithSummary("Changes a person, or deactivates one with active=false.")
            .WithDescription("Administrators only. A change that alters nothing writes and audits nothing. 409 last_admin when the last active administrator would be deactivated or demoted. Needs If-Match with the ETag of the person you read: another version is 412 precondition_failed (also for a change that would change nothing), and a change that changes nothing keeps the version.")
            .Produces<UserResponse>(StatusCodes.Status200OK)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPut("/api/v2/users/{id}/browser-notifications", SetBrowserNotificationsAsync)
            .WithTags(UsersTag)
            .RequireActor()
            .RequireIfMatch()
            .Accepts<BrowserNotificationsBody>("application/json")
            .WithName("setUserBrowserNotifications")
            .WithSummary("Sets the browser notification moments of a person.")
            .WithDescription("A person sets their own moments, an administrator anyone's (403 permission_denied otherwise, which the use case decides after the header check: a request without If-Match is 428 first). The complete setting replaces the stored one; an equal setting writes and audits nothing. The setting is a field of the person, so it needs If-Match with the ETag of the person (GET /users/{id}); another version is 412 precondition_failed, and a setting that changes nothing keeps the version.")
            .Produces<UserResponse>(StatusCodes.Status200OK)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        string? active, string? limit, string? cursor, IUserService users, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        bool? activeFilter = null;
        if (active is not null)
        {
            if (bool.TryParse(active, out var parsed) && (active == "true" || active == "false"))
            {
                activeFilter = parsed;
            }
            else
            {
                errors["active"] = ["must be true or false"];
            }
        }

        int? pageSize = null;
        if (limit is not null)
        {
            if (int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit))
            {
                pageSize = parsedLimit;
            }
            else
            {
                errors["limit"] = ["must be a whole number"];
            }
        }

        if (errors.Count > 0)
        {
            return ProblemResults.From(new ValidationErrors(errors));
        }

        var result = await users.ListAsync(activeFilter, cursor, pageSize, cancellationToken);
        return result.Match<IResult>(
            page => Results.Ok(new UserListResponse([.. page.Items.Select(UserResponse.From)], page.NextCursor)),
            ProblemResults.From,
            error => ProblemResults.From(error, loggers.CreateLogger(typeof(UserEndpoints))));
    }

    private static async Task<IResult> GetAsync(
        string id, HttpContext http, IUserService users, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var result = await users.GetAsync(id, cancellationToken);
        return result.Match<IResult>(
            user =>
            {
                ETags.Set(http.Response, user.Version);
                return Results.Ok(UserResponse.From(user));
            },
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, loggers.CreateLogger(typeof(UserEndpoints))));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http, IUserService users, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (await http.GetActorAsync() is not { } actor)
        {
            return ProfileRequired();
        }

        var body = await UserRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (UserRequestParser.ParseCreate(json, out var invalid) is not { } input)
        {
            return ProblemResults.From(invalid!);
        }

        var result = await users.CreateAsync(actor, input, cancellationToken);
        var logger = loggers.CreateLogger(typeof(UserEndpoints));
        return result.Match<IResult>(
            user =>
            {
                ETags.Set(http.Response, user.Version);
                return Results.Json(UserResponse.From(user), statusCode: StatusCodes.Status201Created);
            },
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> UpdateAsync(
        string id, HttpContext http, IUserService users, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (await http.GetActorAsync() is not { } actor)
        {
            return ProfileRequired();
        }

        var body = await UserRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (UserRequestParser.ParsePatch(json, out var invalid) is not { } input)
        {
            return ProblemResults.From(invalid!);
        }

        var result = await users.UpdateAsync(actor, id, input, cancellationToken, http.GetIfMatch());
        var logger = loggers.CreateLogger(typeof(UserEndpoints));
        return result.Match<IResult>(
            user =>
            {
                ETags.Set(http.Response, user.Version);
                return Results.Ok(UserResponse.From(user));
            },
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger),
            ProblemResults.From);
    }

    private static async Task<IResult> SetBrowserNotificationsAsync(
        string id, HttpContext http, IUserService users, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (await http.GetActorAsync() is not { } actor)
        {
            return ProfileRequired();
        }

        var body = await UserRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (UserRequestParser.ParseNotifications(json, out var invalid) is not { } input)
        {
            return ProblemResults.From(invalid!);
        }

        var result = await users.SetBrowserNotificationsAsync(actor, id, input, cancellationToken, http.GetIfMatch());
        var logger = loggers.CreateLogger(typeof(UserEndpoints));
        return result.Match<IResult>(
            user =>
            {
                ETags.Set(http.Response, user.Version);
                return Results.Ok(UserResponse.From(user));
            },
            ProblemResults.From,
            ProblemResults.From,
            forbidden => ProblemResults.Problem(StatusCodes.Status403Forbidden, ProblemTypes.PermissionDenied, forbidden.Detail),
            ProblemResults.From,
            error => ProblemResults.From(error, logger),
            ProblemResults.From);
    }

    private static IResult ProfileRequired() =>
        ProblemResults.Problem(StatusCodes.Status400BadRequest, ProblemTypes.ProfileRequired, ProfileHeaderAuthenticationHandler.ProfileRequiredDetail);
}
