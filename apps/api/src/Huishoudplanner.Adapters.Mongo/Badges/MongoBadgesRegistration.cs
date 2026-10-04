using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Badges;

public static class MongoBadgesRegistration
{
    /// <summary>
    /// Registers the badge store, the award store and the reader of the data the rules count, plus the transaction runner they run in (registered with
    /// <c>TryAdd</c>, so any slice may ask for it). Needs <see cref="MongoAdapterRegistration.AddMongoAdapter"/>.
    /// </summary>
    public static IServiceCollection AddMongoBadges(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ForRunningTransactions>(sp =>
            new MongoTransactionRunner(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ForStoringBadges>(sp =>
            new MongoBadgeStore(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        services.AddSingleton<ForStoringBadgeAwards>(sp =>
            new MongoBadgeAwardStore(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        services.AddSingleton<ForReadingBadgeEvidence>(sp =>
            new MongoBadgeEvidenceStore(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        return services;
    }
}
