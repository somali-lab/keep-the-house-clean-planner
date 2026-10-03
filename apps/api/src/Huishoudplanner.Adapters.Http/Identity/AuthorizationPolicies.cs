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

internal sealed class MinimumRoleHandler : AuthorizationHandler<MinimumRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MinimumRoleRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true && ActorContext.RoleOf(context.User) is { } role && role >= requirement.Minimum)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
