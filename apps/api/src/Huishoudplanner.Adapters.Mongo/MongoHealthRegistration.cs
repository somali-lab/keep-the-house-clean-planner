using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

public static class MongoHealthRegistration
{
    /// <summary>Registers the shared client (once) and <see cref="ForCheckingHealth"/>. <paramref name="options"/> is resolved lazily, so configuration is read after the host is built.</summary>
    public static IServiceCollection AddMongoHealthCheck(this IServiceCollection services, Func<IServiceProvider, MongoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton<IMongoClient>(sp => MongoClientFactory.CreateClient(options(sp)));
        services.AddSingleton<ForCheckingHealth>(sp => new MongoHealthCheck(sp.GetRequiredService<IMongoClient>(), options(sp)));
        return services;
    }
}
