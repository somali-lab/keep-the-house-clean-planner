using Huishoudplanner.Adapters.Http.Occurrences;
using Huishoudplanner.Application.Occurrences;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.Occurrences;

/// <summary>
/// The wiring of the occurrence actions slice: the use cases and the endpoints. The stores it needs (occurrences, tasks, people, settings, cycles)
/// and the transaction runner are registered by the slices that own them.
/// </summary>
public static class OccurrencesComposition
{
    public static IServiceCollection AddOccurrences(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IOccurrenceService, OccurrenceService>();
        return services;
    }

    public static IEndpointRouteBuilder MapOccurrences(this IEndpointRouteBuilder routes) => routes.MapOccurrenceEndpoints();
}
