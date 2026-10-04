using Huishoudplanner.Adapters.Http.Badges;
using Huishoudplanner.Adapters.Mongo.Badges;
using Huishoudplanner.Application.Badges;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.Badges;

/// <summary>
/// The wiring of the badges slice (ADR-0014): the use cases, the Mongo stores and the endpoints. The award evaluation (<see cref="IBadgeAwardService"/>)
/// is a singleton like the points service that calls it after every execution sync and as the last step of its reconciliation.
/// </summary>
public static class BadgesComposition
{
    public static IServiceCollection AddBadges(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoBadges();
        services.AddSingleton<IBadgeAwardService, BadgeAwardService>();
        services.AddScoped<IBadgeService, BadgeService>();
        return services;
    }

    public static IEndpointRouteBuilder MapBadges(this IEndpointRouteBuilder routes) => routes.MapBadgeEndpoints();
}
