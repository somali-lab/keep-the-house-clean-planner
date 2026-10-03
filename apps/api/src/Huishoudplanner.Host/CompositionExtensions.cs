using System.Reflection;
using Huishoudplanner.Adapters.Http;
using Huishoudplanner.Adapters.Http.Calendar;
using Huishoudplanner.Adapters.Http.Health;
using Huishoudplanner.Adapters.Http.Users;
using Huishoudplanner.Adapters.Http.Meta;
using Huishoudplanner.Adapters.Http.WebApp;
using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Application;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Adapters.Http.Rooms;
using Huishoudplanner.Host.Configuration;
using Huishoudplanner.Host.Startup;
using Huishoudplanner.Host.Users;
using Huishoudplanner.Host.Rooms;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

namespace Huishoudplanner.Host;

/// <summary>The composition root: the only place that knows every adapter.</summary>
public static class CompositionExtensions
{
    public static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpAdapter();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new AppVersion(CurrentVersion()));
        services.AddScoped<IHealthService, HealthService>();
        services.AddSingleton<IMetaService, MetaService>();
        services.AddSingleton(sp => new HouseholdOptions(sp.GetRequiredService<IOptions<AppOptions>>().Value.Timezone));
        services.AddScoped<ICalendarService, CalendarService>();
        services.AddMongoAdapter(sp => sp.GetRequiredService<IOptions<AppOptions>>().Value.MongoUrl);
        services.AddUsers();
        services.AddStartup();
        services.AddSettings();
        services.AddRooms();
        return services;
    }

    public static WebApplication UseApiPipeline(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseHttpAdapter();
        app.MapHealthEndpoints();
        app.MapUserEndpoints();
        app.MapRoomEndpoints();
        app.MapMetaEndpoints();
        app.MapCalendarEndpoints();
        app.MapSettings();
        if (app.Environment.IsDevelopment())
        {
            // The document at /openapi/v2.json and its Scalar UI at /scalar/v2 exist in Development only; the checked-in
            // apps/api/openapi/v2.json (generated at build time) is the artefact everything else uses.
            app.MapOpenApi();
            app.MapScalarApiReference();
        }
        // Mapped endpoints win over the SPA fallback, so the document and the UI are never shadowed by index.html.
        if (!BuildTimeGeneration.IsRunning)
        {
            // No configuration at build time (and the document describes no web app routes: the fallback is excluded from it).
            app.UseWebApp(app.Services.GetRequiredService<IOptions<AppOptions>>().Value.WebDistDir);
        }

        return app;
    }

    /// <summary>The version of version.txt (stamped into the assembly by Directory.Build.props), without build metadata.</summary>
    internal static string CurrentVersion()
    {
        var informational = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+')[0] ?? "0.0.0";
    }
}
