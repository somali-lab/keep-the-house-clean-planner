using System.Globalization;
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

        routes.MapPost("/api/v2/users", CreateAsync)
            .WithTags(UsersTag)
            .RequireAdmin()
            .WithName("createUser")
            .WithSummary("Creates a person.")
            .WithDescription("Administrators only. Audited as user/create.")
            .Produces<UserResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPatch("/api/v2/users/{id}", UpdateAsync)
            .WithTags(UsersTag)
            .RequireAdmin()
            .WithName("updateUser")
            .WithSummary("Changes a person, or deactivates one with active=false.")
            .WithDescription("Administrators only. A change that alters nothing writes and audits nothing. 409 last_admin when the last active administrator would be deactivated or demoted.")
            .Produces<UserResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPut("/api/v2/users/{id}/browser-notifications", SetBrowserNotificationsAsync)
            .WithTags(UsersTag)
            .RequireActor()
            .WithName("setUserBrowserNotifications")
            .WithSummary("Sets the browser notification moments of a person.")
            .WithDescription("A person sets their own moments, an administrator anyone's (403 permission_denied otherwise). The complete setting replaces the stored one; an equal setting writes and audits nothing.")
            .Produces<UserResponse>(StatusCodes.Status200OK)
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

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request, HttpContext http, IUserService users, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (await http.GetActorAsync() is not { } actor)
        {
            return ProfileRequired();
        }

        var result = await users.CreateAsync(actor, request.ToInput(), cancellationToken);
        var logger = loggers.CreateLogger(typeof(UserEndpoints));
        return result.Match<IResult>(
            user => Results.Json(UserResponse.From(user), statusCode: StatusCodes.Status201Created),
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> UpdateAsync(
        string id, UpdateUserRequest request, HttpContext http, IUserService users, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (await http.GetActorAsync() is not { } actor)
        {
            return ProfileRequired();
        }

        var result = await users.UpdateAsync(actor, id, request.ToInput(), cancellationToken);
        var logger = loggers.CreateLogger(typeof(UserEndpoints));
        return result.Match<IResult>(
            user => Results.Ok(UserResponse.From(user)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> SetBrowserNotificationsAsync(
        string id, BrowserNotificationsBody request, HttpContext http, IUserService users, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (await http.GetActorAsync() is not { } actor)
        {
            return ProfileRequired();
        }

        var result = await users.SetBrowserNotificationsAsync(actor, id, request.ToInput(), cancellationToken);
        var logger = loggers.CreateLogger(typeof(UserEndpoints));
        return result.Match<IResult>(
            user => Results.Ok(UserResponse.From(user)),
            ProblemResults.From,
            ProblemResults.From,
            forbidden => ProblemResults.Problem(StatusCodes.Status403Forbidden, ProblemTypes.PermissionDenied, forbidden.Detail),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static IResult ProfileRequired() =>
        ProblemResults.Problem(StatusCodes.Status400BadRequest, ProblemTypes.ProfileRequired, ProfileHeaderAuthenticationHandler.ProfileRequiredDetail);
}
