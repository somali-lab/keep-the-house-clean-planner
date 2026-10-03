using System.Reflection;
using Huishoudplanner.Adapters.Http;
using Huishoudplanner.Adapters.Http.Health;
using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Application;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Host.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Huishoudplanner.Host;

/// <summary>The composition root: the only place that knows every adapter.</summary>
public static class CompositionExtensions
{
    private const string DefaultDatabaseName = "huishoudplanner";

    public static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpAdapter();
        services.AddSingleton(new AppVersion(CurrentVersion()));
        services.AddScoped<IHealthService, HealthService>();
        services.AddMongoHealthCheck(sp => ToMongoOptions(sp.GetRequiredService<IOptions<AppOptions>>().Value));
        return services;
    }

    public static WebApplication UseApiPipeline(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseHttpAdapter();
        app.MapHealthEndpoints();
        return app;
    }

    /// <summary>The database is named in the connection string (<c>mongodb://host/name</c>), as in the Node server.</summary>
    internal static MongoOptions ToMongoOptions(AppOptions options) => new()
    {
        ConnectionString = options.MongoUrl,
        DatabaseName = MongoUrl.Create(options.MongoUrl).DatabaseName ?? DefaultDatabaseName,
    };

    /// <summary>The version of version.txt (stamped into the assembly by Directory.Build.props), without build metadata.</summary>
    internal static string CurrentVersion()
    {
        var informational = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+')[0] ?? "0.0.0";
    }
}
