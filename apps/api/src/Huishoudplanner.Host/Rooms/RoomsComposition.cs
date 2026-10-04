using Huishoudplanner.Adapters.Mongo.Rooms;
using Huishoudplanner.Application.Rooms;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Host.Startup;

namespace Huishoudplanner.Host.Rooms;

/// <summary>The wiring of the rooms slice: the use cases and the driven ports they need.</summary>
public static class RoomsComposition
{
    public static IServiceCollection AddRooms(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoRooms();
        services.AddScoped<IRoomService, RoomService>();
        services.AddSingleton<IRoomSeedService, RoomSeedService>();
        services.AddSingleton<ISeedStep, RoomSeedStep>();
        return services;
    }
}

/// <summary>Seeds the default rooms on an empty rooms collection (the rooms step of <c>seed()</c> in apps/server/src/domain/seed.ts) and logs how many were created.</summary>
internal sealed partial class RoomSeedStep(IRoomSeedService seed, ILogger<RoomSeedStep> logger) : ISeedStep
{
    public string Name => "rooms";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await seed.SeedAsync(cancellationToken).ConfigureAwait(false);
        result.Switch(
            created =>
            {
                if (created > 0)
                {
                    LogSeeded(logger, created);
                }
            },
            conflict => throw new InvalidOperationException($"Startup failed while seeding the rooms: {conflict.Code}"),
            error => throw new InvalidOperationException($"Startup failed while seeding the rooms: {error.Message}"));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {Count} rooms")]
    private static partial void LogSeeded(ILogger logger, int count);
}
