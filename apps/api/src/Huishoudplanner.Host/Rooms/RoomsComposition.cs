using Huishoudplanner.Adapters.Mongo.Rooms;
using Huishoudplanner.Application.Rooms;
using Huishoudplanner.Domain.Ports.Driving;

namespace Huishoudplanner.Host.Rooms;

/// <summary>The wiring of the rooms slice: the use cases and the driven ports they need.</summary>
public static class RoomsComposition
{
    public static IServiceCollection AddRooms(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoRooms();
        services.AddScoped<IRoomService, RoomService>();
        return services;
    }
}
