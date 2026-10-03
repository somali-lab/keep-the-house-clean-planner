using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Promotion;

public static class MongoPromoteRegistration
{
    /// <summary>Registers the read-only occurrence reader of the promote suggestions. Needs <see cref="MongoAdapterRegistration.AddMongoAdapter"/>.</summary>
    public static IServiceCollection AddMongoPromote(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ForReadingPromotionEvidence>(sp =>
            new MongoPromotionEvidenceReader(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        return services;
    }
}
