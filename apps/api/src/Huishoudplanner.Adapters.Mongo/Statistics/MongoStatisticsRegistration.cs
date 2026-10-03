using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Statistics;

public static class MongoStatisticsRegistration
{
    /// <summary>
    /// Registers the statistics reads and the statistics reset, plus the transaction runner the reset runs in (registered with <c>TryAdd</c>, so any
    /// slice may ask for it). Needs <see cref="MongoAdapterRegistration.AddMongoAdapter"/>.
    /// </summary>
    public static IServiceCollection AddMongoStatistics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ForRunningTransactions>(sp =>
            new MongoTransactionRunner(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ForReadingStatistics>(sp =>
            new MongoStatisticsReader(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        services.AddSingleton<ForResettingStatistics>(sp =>
            new MongoStatisticsResetStore(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        return services;
    }
}
