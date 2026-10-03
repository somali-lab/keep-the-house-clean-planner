using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Points;

public static class MongoPlannedWorkRegistration
{
    /// <summary>Registers the read of the planned work for the reward meter. Needs <see cref="MongoAdapterRegistration.AddMongoAdapter"/>.</summary>
    public static IServiceCollection AddMongoPlannedWork(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ForReadingPlannedWork>(sp =>
            new MongoPlannedWorkReader(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        return services;
    }
}
