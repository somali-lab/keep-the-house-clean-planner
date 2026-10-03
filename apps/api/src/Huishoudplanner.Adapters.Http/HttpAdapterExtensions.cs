using System.Diagnostics;
using Huishoudplanner.Adapters.Http.Problems;
using Microsoft.AspNetCore.Http.Extensions;

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
        return services;
    }

    /// <summary>Exceptions and empty 4xx/5xx responses become <c>application/problem+json</c>. Call first in the pipeline.</summary>
    public static IApplicationBuilder UseHttpAdapter(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }
}
