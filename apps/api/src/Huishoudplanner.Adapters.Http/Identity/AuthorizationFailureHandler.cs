using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Huishoudplanner.Adapters.Http.Identity;

/// <summary>
/// Answers a failed policy with the codes of requirements section 8: no actor is <c>400 profile_required</c> (a profile
/// is attribution, not a login, so never 401), an actor with too low a role is <c>403 permission_denied</c>. The decision
/// is made from the resolved actor, not from the challenge/forbid flag, because the scheme never authenticates (see
/// <see cref="ProfileHeaderAuthenticationHandler"/>).
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

        if (authorizeResult.Succeeded)
        {
            await fallback.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        if (await context.GetActorAsync() is null)
        {
            await ProblemResults.Problem(
                StatusCodes.Status400BadRequest,
                ProblemTypes.ProfileRequired,
                ProfileHeaderAuthenticationHandler.ProfileRequiredDetail).ExecuteAsync(context);
            return;
        }

        // The highest role the endpoint asks for, when it carries several policies.
        var minimum = policy.Requirements.OfType<MinimumRoleRequirement>().Select(r => (Role?)r.Minimum).Max()?.ToString().ToLowerInvariant();
        await ProblemResults.Problem(
            StatusCodes.Status403Forbidden,
            ProblemTypes.PermissionDenied,
            minimum is null ? ProfileHeaderAuthenticationHandler.RoleTooLowDetail : $"The {minimum} role is required.").ExecuteAsync(context);
    }
}
