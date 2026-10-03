using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// A test seam on the client creation: the integration tests register one to watch the commands the application sends
/// (the write capture of the audit coverage tests). Nothing registers it in production, so it costs nothing there.
/// </summary>
internal interface IMongoClientSettingsCustomizer
{
    void Customize(MongoClientSettings settings);
}
