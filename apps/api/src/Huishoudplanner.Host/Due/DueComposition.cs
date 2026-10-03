using Huishoudplanner.Adapters.Http.Due;
using Huishoudplanner.Adapters.Mongo.Due;
using Huishoudplanner.Application.Due;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.Due;

/// <summary>The wiring of the due engine slice: the use case, its occurrence reader and the endpoint. It reads tasks, rooms and settings through the ports of their slices.</summary>
public static class DueComposition
{
    public static IServiceCollection AddDue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoDue();
        services.AddScoped<IDueService, DueService>();
        return services;
    }

    public static IEndpointRouteBuilder MapDue(this IEndpointRouteBuilder routes) => routes.MapDueEndpoints();
}
