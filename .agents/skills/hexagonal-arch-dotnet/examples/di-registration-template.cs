// Worked example: DI registration per project for the household planner.
// Each project owns one static DependencyInjection class with one extension method on
// IServiceCollection. Application maps driving ports (IXxxService) to use cases; each adapter maps
// the driven ports (ForXxx) it implements. Host (Program.cs) is the composition root and only
// calls these methods. Illustrative: it need not compile, but the wiring pattern is the rule.

using Microsoft.Extensions.DependencyInjection;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;

// --- Application: Huishoudplanner.Application/DependencyInjection.cs --------------------------
namespace Huishoudplanner.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Use cases depend on driven ports and TimeProvider only, never on DateTime.UtcNow.
        services.AddScoped<IOccurrenceService, OccurrenceService>();
        services.AddScoped<ICyclePlanService, CyclePlanService>();
        return services;
    }
}

// --- Mongo adapter: Huishoudplanner.Adapters.Mongo/DependencyInjection.cs ---------------------
namespace Huishoudplanner.Adapters.Mongo;

public static class DependencyInjection
{
    public static IServiceCollection AddMongoAdapter(this IServiceCollection services)
    {
        // Options are bound and validated in Host (ValidateOnStart, no values in error messages).
        // One idempotent class-map registration; no Bson attributes on domain types.
        MongoClassMaps.Register();

        services.AddSingleton<IMongoClient>(sp => CreateClient(sp));
        services.AddScoped<ForRunningTransactions, MongoTransactionRunner>();
        services.AddScoped<ForRecordingAudit, MongoAuditWriter>();
        services.AddScoped<ForStoringOccurrences, StoreOccurrencesInMongo>();
        services.AddHostedService<IndexEnsurerAndMigrations>();
        return services;
    }
}

// --- Host: Huishoudplanner.Host/Program.cs -----------------------------------------------------
// builder.Services.AddSingleton(TimeProvider.System);   // production clock
// builder.Services.AddApplication();
// builder.Services.AddMongoAdapter();
// builder.Services.AddHttpAdapter();                    // identity adapter, policies, Problem Details
// ...
// app.MapOccurrenceEndpoints();                         // endpoint group from Adapters.Http
