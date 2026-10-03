using Huishoudplanner.Adapters.Http.Health;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Huishoudplanner.Adapters.Http.OpenApi;

/// <summary>The v2 OpenAPI document: its name, metadata and tags. Endpoints describe themselves with metadata.</summary>
public static class OpenApiSetup
{
    /// <summary>The document name; also the file name <c>v2.json</c> and the route <c>/openapi/v2.json</c>.</summary>
    public const string DocumentName = "v2";

    public const string HealthTag = "Health";

    public const string MetaTag = "Meta";

    public const string CalendarTag = "Calendar";

    public const string RoomsTag = "Rooms";

    public static IServiceCollection AddOpenApiDocument(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOpenApi(DocumentName, options => options.AddDocumentTransformer((document, context, _) =>
        {
            var version = context.ApplicationServices.GetRequiredService<AppVersion>().Value;
            document.Info = new OpenApiInfo
            {
                Title = "Huishoudplanner API",
                Version = version,
                Description = "The v2 HTTP API of Keep the House Clean Planner. Errors are RFC 9457 Problem Details (application/problem+json).",
            };

            // Paths already carry the /api/v2 prefix, so the only server is the origin itself. Set explicitly: the
            // runtime document would otherwise list the request host, and build time and runtime must be identical.
            document.Servers = [new OpenApiServer { Url = "/" }];
            document.Tags = new HashSet<OpenApiTag>
            {
                new() { Name = HealthTag, Description = "Liveness and database reachability, for load balancers and the container healthcheck." },
                new() { Name = MetaTag, Description = "Limits and defaults the web app reads once per session." },
                new() { Name = CalendarTag, Description = "Cycle, week and ISO week of calendar days." },
                new() { Name = RoomsTag, Description = "The rooms of the house: everyone reads them, administrators create, change and delete them." },
            };
            return Task.CompletedTask;
        }));
        return services;
    }
}
