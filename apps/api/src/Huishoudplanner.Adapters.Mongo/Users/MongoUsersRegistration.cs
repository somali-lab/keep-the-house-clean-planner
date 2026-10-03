using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Users;

public static class MongoUsersRegistration
{
    /// <summary>
    /// Registers the users store as <see cref="ForStoringUsers"/> and as the identity lookup <see cref="ForFindingUsers"/>
    /// (one instance, one collection). Needs the client and options of <see cref="MongoAdapterRegistration.AddMongoAdapter"/>, which calls this.
    /// </summary>
    public static IServiceCollection AddMongoUsers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(sp => new MongoUserStore(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        services.AddSingleton<ForStoringUsers>(sp => sp.GetRequiredService<MongoUserStore>());
        services.AddSingleton<ForFindingUsers>(sp => sp.GetRequiredService<MongoUserStore>());
        return services;
    }
}
