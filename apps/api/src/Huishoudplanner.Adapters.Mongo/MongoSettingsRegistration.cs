using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

public static class MongoSettingsRegistration
{
    /// <summary>
    /// Registers the settings document store as <see cref="ForStoringSettings"/> and <see cref="ForReadingCycleAnchor"/> (one instance), and the
    /// read of the intervals tasks use. Needs the client and options of <see cref="MongoAdapterRegistration.AddMongoAdapter"/> and the host's <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddMongoSettings(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // The transaction runner is shared by every use case; the first slice that needs it registers it (TryAdd keeps a second registration harmless).
        services.TryAddSingleton<ForRunningTransactions>(sp =>
            new MongoTransactionRunner(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new MongoSettingsStore(
            sp.GetRequiredService<IMongoClient>(),
            sp.GetRequiredService<MongoOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ForStoringSettings>(sp => sp.GetRequiredService<MongoSettingsStore>());
        services.AddSingleton<ForReadingCycleAnchor>(sp => sp.GetRequiredService<MongoSettingsStore>());
        services.AddSingleton<ForCheckingIntervalUsage>(sp =>
            new MongoIntervalUsage(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        return services;
    }
}
