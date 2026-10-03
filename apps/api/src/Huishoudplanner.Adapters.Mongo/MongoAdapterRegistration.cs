using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

public static class MongoAdapterRegistration
{
    private const string DefaultDatabaseName = "huishoudplanner";

    /// <summary>
    /// Registers the shared client and the driven ports this adapter implements. Driver-free for the caller:
    /// <paramref name="connectionString"/> is resolved lazily (after configuration is bound) and the database name is
    /// taken from its path (<c>mongodb://host/name</c>, as in the Node server) unless <paramref name="databaseName"/> is given.
    /// </summary>
    public static IServiceCollection AddMongoAdapter(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString,
        string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionString);

        services.TryAddSingleton(sp =>
        {
            var url = connectionString(sp);
            return new MongoOptions
            {
                ConnectionString = url,
                DatabaseName = databaseName ?? MongoUrl.Create(url).DatabaseName ?? DefaultDatabaseName,
            };
        });
        services.TryAddSingleton<IMongoClient>(sp => MongoClientFactory.CreateClient(sp.GetRequiredService<MongoOptions>()));
        services.AddSingleton<ForCheckingHealth>(sp =>
            new MongoHealthCheck(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoOptions>()));
        return services;
    }
}
