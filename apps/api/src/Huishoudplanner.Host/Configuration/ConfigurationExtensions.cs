using Microsoft.Extensions.Options;

namespace Huishoudplanner.Host.Configuration;

public static class ConfigurationExtensions
{
    /// <summary>
    /// Makes the legacy LOG_LEVEL feed the .NET logging configuration when Logging__LogLevel__Default
    /// is not set. Call after the environment variables are added.
    /// </summary>
    public static IConfigurationBuilder AddLegacyEnvironmentAliases(this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var overrides = AppOptionsBinder.LegacyLogLevelOverride(builder.Build());
        return overrides.Count == 0 ? builder : builder.AddInMemoryCollection(overrides);
    }

    /// <summary>
    /// Binds <see cref="AppOptions"/> from <paramref name="configuration"/> and validates it when the
    /// host starts, so an invalid configuration refuses to start without echoing any value.
    /// </summary>
    public static IServiceCollection AddAppOptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<IValidateOptions<AppOptions>, AppOptionsValidator>();
        services.AddOptions<AppOptions>()
            .Configure(options => AppOptionsBinder.Bind(configuration, options))
            .ValidateOnStart();
        return services;
    }
}
