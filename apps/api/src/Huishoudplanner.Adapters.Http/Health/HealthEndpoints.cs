using Huishoudplanner.Adapters.Http.OpenApi;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Adapters.Http.Health;

/// <summary>The version the running instance reports. Created by the composition root.</summary>
public sealed record AppVersion(string Value);

/// <summary>
/// The v2 health body. Same idea as v1 (<c>{ status, mongo }</c>) with a version and a database-neutral field name.
/// Not a Problem Details body: load balancers and the container healthcheck only read the status code and these two fields.
/// </summary>
public sealed record HealthResponse(string Status, string Version, string Database);

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/api/v2/health", async (IHealthService health, AppVersion version, CancellationToken cancellationToken) =>
        {
            var report = await health.GetReportAsync(cancellationToken);
            var body = new HealthResponse(report.IsHealthy ? "ok" : "error", version.Value, report.DatabaseReachable ? "ok" : "error");
            return Results.Json(body, statusCode: report.IsHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        })
        .WithName("getHealth")
        .WithTags(OpenApiSetup.HealthTag)
        .WithSummary("Reports whether the service and its database are reachable.")
        .WithDescription("Answers 200 when the database answers a ping and 503 when it does not; both carry the same body shape.")
        .Produces<HealthResponse>(StatusCodes.Status200OK)
        .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable)
        .ProducesProblem(StatusCodes.Status500InternalServerError);
        return routes;
    }
}
