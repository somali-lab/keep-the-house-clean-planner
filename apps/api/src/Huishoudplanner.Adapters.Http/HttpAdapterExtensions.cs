using System.Diagnostics;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Huishoudplanner.Adapters.Http;

public static class HttpAdapterExtensions
{
    /// <summary>Registers Problem Details: every problem gets a stable <c>type</c> URN, a <c>detail</c>, a <c>status</c> and a <c>traceId</c>.</summary>
    public static IServiceCollection AddHttpAdapter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            var status = problem.Status ?? context.HttpContext.Response.StatusCode;
            problem.Status = status;
            if (problem.Type is null || !problem.Type.StartsWith(ProblemTypes.Prefix, StringComparison.Ordinal))
            {
                problem.Type = ProblemTypes.UrnFor(ProblemTypes.CodeForStatus(status));
            }

            // An unhandled exception never leaks its message.
            problem.Detail ??= status >= StatusCodes.Status500InternalServerError
                ? ProblemResults.UnexpectedErrorDetail
                : problem.Title;
            problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        });
        services.AddIdentity();
        return services;
    }

    /// <summary>
    /// Identity (ADR-0018): the profile-header authentication scheme, the actor resolver and the three policies.
    /// Needs a <see cref="ForFindingUsers"/> registration from the composition root; <see cref="UseHttpAdapter"/> checks it at startup.
    /// </summary>
    private static void AddIdentity(this IServiceCollection services)
    {
        services.TryAddScoped<ForResolvingActors, ProfileHeaderActorResolver>();
        services.AddAuthentication(ProfileHeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ProfileHeaderAuthenticationHandler>(ProfileHeaderAuthenticationHandler.SchemeName, null);
        services.AddAuthorizationBuilder().AddHouseholdPolicies();
        services.AddSingleton<IAuthorizationHandler, MinimumRoleHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuthorizationFailureHandler>();
    }

    /// <summary>Exceptions and empty 4xx/5xx responses become <c>application/problem+json</c>. Call first in the pipeline.</summary>
    public static IApplicationBuilder UseHttpAdapter(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        RequireUserPort(app.ApplicationServices);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        // The authentication handler resolves the actor only for endpoints that declare authorization (see ProfileHeaderAuthenticationHandler),
        // so endpoints without a policy (health, static files) never depend on the user lookup.
        app.UseAuthorization();
        return app;
    }

    /// <summary>Fails at startup, not on the first request, when the composition root forgot the user port that identity needs.</summary>
    private static void RequireUserPort(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        try
        {
            _ = scope.ServiceProvider.GetRequiredService<ForResolvingActors>();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "AddHttpAdapter needs a registration of ForFindingUsers (or another ForResolvingActors) from the composition root.", ex);
        }
    }
}
