using Huishoudplanner.Adapters.Http.OpenApi;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Adapters.Http.Meta;

public static class MetaEndpoints
{
    public static IEndpointRouteBuilder MapMetaEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        // Open like the Node reads: no policy, no actor needed.
        routes.MapGet("/api/v2/meta/limits", (IMetaService meta) => Results.Ok(meta.GetLimits()))
            .WithName("getLimits")
            .WithTags(OpenApiSetup.MetaTag)
            .WithSummary("Returns every limit and default the web app needs, grouped by resource.")
            .WithDescription("Read once per session. The values never change while the server runs. The server also applies the default points of a task (one point per minute, between 1 and tasks.maxPoints) when a create request omits points, so the client does not compute it.")
            .Produces<HouseholdLimits>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return routes;
    }
}
