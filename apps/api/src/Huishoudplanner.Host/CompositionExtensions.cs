using System.Reflection;
using Huishoudplanner.Adapters.Http;
using Huishoudplanner.Adapters.Http.Health;
using Huishoudplanner.Adapters.Http.WebApp;
using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Application;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Host.Configuration;
using Microsoft.Extensions.Options;

namespace Huishoudplanner.Host;

/// <summary>The composition root: the only place that knows every adapter.</summary>
public static class CompositionExtensions
{
    public static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpAdapter();
        services.AddSingleton(new AppVersion(CurrentVersion()));
        services.AddScoped<IHealthService, HealthService>();
        services.AddMongoAdapter(sp => sp.GetRequiredService<IOptions<AppOptions>>().Value.MongoUrl);
        return services;
    }

    public static WebApplication UseApiPipeline(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseHttpAdapter();
        app.MapHealthEndpoints();
        app.UseWebApp(app.Services.GetRequiredService<IOptions<AppOptions>>().Value.WebDistDir);
        return app;
    }

    /// <summary>The version of version.txt (stamped into the assembly by Directory.Build.props), without build metadata.</summary>
    internal static string CurrentVersion()
    {
        var informational = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+')[0] ?? "0.0.0";
    }
}
