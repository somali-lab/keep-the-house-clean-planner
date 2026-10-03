using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Tasks;

public static class MongoTasksRegistration
{
    /// <summary>
    /// Registers the task store, which also answers which rooms and intervals tasks use (the room and settings use cases ask it), plus
    /// the transaction runner it runs in (registered with <c>TryAdd</c>, so any slice may ask for it). Needs <see cref="MongoAdapterRegistration.AddMongoAdapter"/>.
    /// </summary>
    public static IServiceCollection AddMongoTasks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ForRunningTransactions>(sp =>
            new MongoTransactionRunner(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ForStoringTasks>(sp =>
            new MongoTaskStore(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        return services;
    }
}
