using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Huishoudplanner.Host.Startup;

public static class StartupRegistration
{
    /// <summary>
    /// Runs the storage preparation and the registered <see cref="ISeedStep"/> list before the host starts listening.
    /// Skipped during build-time OpenAPI generation (no configuration, no database).
    /// </summary>
    public static IServiceCollection AddStartup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!BuildTimeGeneration.IsRunning)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, StartupService>());
        }

        return services;
    }
}
