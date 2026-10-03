using Huishoudplanner.Adapters.Http.Settings;
using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Application.Settings;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Host.Startup;

namespace Huishoudplanner.Host;

/// <summary>Composition of the settings resource: the use cases, the Mongo store, the startup seed and the endpoints.</summary>
public static class SettingsComposition
{
    public static IServiceCollection AddSettings(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMongoSettings();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISettingsSeedService, SettingsSeedService>();
        services.AddSingleton<ISeedStep, SettingsSeedStep>();
        return services;
    }

    public static IEndpointRouteBuilder MapSettings(this IEndpointRouteBuilder routes) => routes.MapSettingsEndpoints();
}
