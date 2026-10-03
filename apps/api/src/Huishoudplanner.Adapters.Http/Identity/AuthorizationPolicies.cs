using Huishoudplanner.Domain.Identity;
using Microsoft.AspNetCore.Authorization;

namespace Huishoudplanner.Adapters.Http.Identity;

/// <summary>The three policies of ADR-0018. Each needs an actor of at least the given role; roles are ordered member, planner, admin.</summary>
public static class AuthorizationPolicies
{
    /// <summary>Every writing endpoint: any active profile.</summary>
    public const string ActorPolicy = "RequireActor";

    /// <summary>Planning, task and optimisation changes.</summary>
    public const string PlannerPolicy = "RequirePlanner";

    /// <summary>Household configuration, people and destructive maintenance.</summary>
    public const string AdminPolicy = "RequireAdmin";

    public static TBuilder RequireActor<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(ActorPolicy);

    public static TBuilder RequirePlanner<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(PlannerPolicy);

    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(AdminPolicy);

    internal static AuthorizationBuilder AddHouseholdPolicies(this AuthorizationBuilder authorization) => authorization
        .AddPolicy(ActorPolicy, Build(Role.Member))
        .AddPolicy(PlannerPolicy, Build(Role.Planner))
        .AddPolicy(AdminPolicy, Build(Role.Admin));

    private static AuthorizationPolicy Build(Role minimum) => new AuthorizationPolicyBuilder(ProfileHeaderAuthenticationHandler.SchemeName)
        .AddRequirements(new MinimumRoleRequirement(minimum))
        .Build();
}

/// <summary>Satisfied by an authenticated actor whose role is at least <paramref name="Minimum"/>.</summary>
internal sealed record MinimumRoleRequirement(Role Minimum) : IAuthorizationRequirement;

/// <summary>
/// Resolves the actor of the request (lazily, once; see <see cref="ActorContext"/>) and succeeds when its role is at least
/// the minimum. A failing user lookup throws here, inside the authorization middleware, so the exception handler answers 500.
/// </summary>
internal sealed class MinimumRoleHandler : AuthorizationHandler<MinimumRoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, MinimumRoleRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
        {
            return;
        }

        var result = await ActorContext.ResolveAsync(http).ConfigureAwait(false);
        var succeeded = result.Match(actor => actor.Role >= requirement.Minimum, _ => false, error => throw ActorContext.FailedLookup(error));
        if (succeeded)
        {
            context.Succeed(requirement);
        }
    }
}
