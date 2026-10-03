using Huishoudplanner.Adapters.Http.Problems;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Huishoudplanner.Adapters.Http.Identity;

/// <summary>
/// Answers a failed policy with the codes of requirements section 8: no actor is <c>400 profile_required</c> (a profile
/// is attribution, not a login, so never 401), an actor with too low a role is <c>403 permission_denied</c>.
/// </summary>
internal sealed class AuthorizationFailureHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Challenged)
        {
            await ProblemResults.Problem(
                StatusCodes.Status400BadRequest,
                ProblemTypes.ProfileRequired,
                "An active profile is required (X-Profile-Id).").ExecuteAsync(context);
            return;
        }

        if (authorizeResult.Forbidden)
        {
            var minimum = policy.Requirements.OfType<MinimumRoleRequirement>().Select(r => r.Minimum.ToString().ToLowerInvariant()).FirstOrDefault();
            await ProblemResults.Problem(
                StatusCodes.Status403Forbidden,
                ProblemTypes.PermissionDenied,
                minimum is null ? "The role is too low." : $"The {minimum} role is required.").ExecuteAsync(context);
            return;
        }

        await fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
