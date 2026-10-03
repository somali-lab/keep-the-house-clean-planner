using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.CyclePlans;

public static class MongoCyclePlansRegistration
{
    /// <summary>
    /// Registers the cycle plan store plus the transaction runner it runs in (registered with <c>TryAdd</c>, so any slice may ask for it).
    /// Needs <see cref="MongoAdapterRegistration.AddMongoAdapter"/>.
    /// </summary>
    public static IServiceCollection AddMongoCyclePlans(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ForRunningTransactions>(sp =>
            new MongoTransactionRunner(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ForStoringCyclePlans>(sp =>
            new MongoCyclePlanStore(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        return services;
    }
}
