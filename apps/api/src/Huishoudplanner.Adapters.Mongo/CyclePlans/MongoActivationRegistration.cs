using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.CyclePlans;

public static class MongoActivationRegistration
{
    /// <summary>
    /// Registers the plan activation writes (with the guard document) and the occurrence read of the activation preview, plus the transaction
    /// runner they run in (registered with <c>TryAdd</c>, so any slice may ask for it). Needs <see cref="MongoAdapterRegistration.AddMongoAdapter"/>.
    /// </summary>
    public static IServiceCollection AddMongoActivation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ForRunningTransactions>(sp =>
            new MongoTransactionRunner(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new MongoPlanActivationStore(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        services.AddSingleton<ForActivatingCyclePlans>(sp => sp.GetRequiredService<MongoPlanActivationStore>());
        services.AddSingleton<ForReadingOccurrencesForActivation>(sp => sp.GetRequiredService<MongoPlanActivationStore>());
        return services;
    }
}
