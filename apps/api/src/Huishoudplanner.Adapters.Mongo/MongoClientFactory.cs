using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

internal static class MongoClientFactory
{
    private static readonly TimeSpan ServerSelectionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Registers the class maps and conventions, then creates the client. Connects lazily on first use.</summary>
    public static IMongoClient CreateClient(MongoOptions options, IEnumerable<IMongoClientSettingsCustomizer>? customizers = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        MongoClassMaps.Register();
        var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
        settings.ServerSelectionTimeout = ServerSelectionTimeout;
        foreach (var customizer in customizers ?? [])
        {
            customizer.Customize(settings);
        }

        return new MongoClient(settings);
    }

    public static IMongoDatabase GetDatabase(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        return client.GetDatabase(options.DatabaseName);
    }
}
