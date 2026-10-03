using Huishoudplanner.Adapters.Http.Promotion;
using Huishoudplanner.Adapters.Mongo.Promotion;
using Huishoudplanner.Application.Promotion;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.Promotion;

/// <summary>The wiring of the promote suggestions slice: the use case, its occurrence reader and the endpoint. It reads settings, plans, cycles and tasks through the ports of their slices.</summary>
public static class PromoteComposition
{
    public static IServiceCollection AddPromote(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoPromote();
        services.AddScoped<IPromoteService, PromoteService>();
        return services;
    }

    public static IEndpointRouteBuilder MapPromote(this IEndpointRouteBuilder routes) => routes.MapPromoteEndpoints();
}
