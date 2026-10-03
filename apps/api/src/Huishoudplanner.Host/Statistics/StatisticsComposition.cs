using Huishoudplanner.Adapters.Http.Statistics;
using Huishoudplanner.Adapters.Mongo.Statistics;
using Huishoudplanner.Application.Statistics;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.Statistics;

/// <summary>The wiring of the statistics slice: the use cases, the statistics reads and the reset store, and the endpoints.</summary>
public static class StatisticsComposition
{
    public static IServiceCollection AddStatistics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoStatistics();
        services.AddScoped<IStatisticsService, StatisticsService>();
        return services;
    }

    public static IEndpointRouteBuilder MapStatistics(this IEndpointRouteBuilder routes) => routes.MapStatisticsEndpoints();
}
