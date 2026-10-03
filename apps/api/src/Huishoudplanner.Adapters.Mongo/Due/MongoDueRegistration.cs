using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Due;

public static class MongoDueRegistration
{
    /// <summary>Registers the read-only occurrence reader of the due list. Needs <see cref="MongoAdapterRegistration.AddMongoAdapter"/>.</summary>
    public static IServiceCollection AddMongoDue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ForReadingDueOccurrences>(sp =>
            new MongoDueOccurrenceReader(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        return services;
    }
}
