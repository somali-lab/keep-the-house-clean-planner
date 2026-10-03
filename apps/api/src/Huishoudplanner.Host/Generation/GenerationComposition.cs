using Huishoudplanner.Adapters.Http.Cycles;
using Huishoudplanner.Adapters.Mongo.Cycles;
using Huishoudplanner.Adapters.Mongo.Occurrences;
using Huishoudplanner.Application.Generation;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.Generation;

/// <summary>The wiring of the cycles and generation slice: the use cases, the cycle and occurrence stores and the cycle list endpoint.</summary>
public static class GenerationComposition
{
    public static IServiceCollection AddGeneration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoCycles();
        services.AddMongoOccurrences();
        services.AddScoped<ICycleService, CycleService>();
        services.AddScoped<IGenerationService, GenerationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapGeneration(this IEndpointRouteBuilder routes) => routes.MapCycleEndpoints();
}
