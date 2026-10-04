using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Transfer;

public static class MongoTransferRegistration
{
    /// <summary>
    /// Registers the JSON export and import of the whole dataset, plus the transaction runner the import runs in (registered with <c>TryAdd</c>, so any
    /// slice may ask for it). Needs <see cref="MongoAdapterRegistration.AddMongoAdapter"/>.
    /// </summary>
    public static IServiceCollection AddMongoTransfer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ForRunningTransactions>(sp =>
            new MongoTransactionRunner(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ForTransferringData>(sp =>
            new MongoTransferStore(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        return services;
    }
}
