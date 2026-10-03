using Huishoudplanner.Adapters.Http.Points;
using Huishoudplanner.Adapters.Mongo.Points;
using Huishoudplanner.Application.Points;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.Points;

/// <summary>The wiring of the reward meter: the progress use case, the read of the planned work and the endpoint.</summary>
public static class RewardProgressComposition
{
    public static IServiceCollection AddRewardProgress(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoPlannedWork();
        services.AddSingleton<IRewardProgressService, RewardProgressService>();
        return services;
    }

    public static IEndpointRouteBuilder MapRewardProgress(this IEndpointRouteBuilder routes) => routes.MapRewardProgressEndpoints();
}
