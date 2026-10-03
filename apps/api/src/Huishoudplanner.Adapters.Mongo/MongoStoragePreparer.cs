using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// <see cref="ForPreparingStorage"/>: the pending migrations first (the legacy slot index drop must precede the index
/// ensurer, which would otherwise conflict with it), then every collection and index. Both are idempotent.
/// </summary>
internal sealed class MongoStoragePreparer(IMongoClient client, MongoOptions options, TimeProvider timeProvider) : ForPreparingStorage
{
    public async Task<OneOf<Success, PortError>> PrepareAsync(CancellationToken cancellationToken)
    {
        var database = MongoClientFactory.GetDatabase(client, options);
        try
        {
            await MigrationRunner.CreateDefault(database, timeProvider).RunAsync(cancellationToken).ConfigureAwait(false);
            await new IndexEnsurer(database).EnsureAsync(cancellationToken).ConfigureAwait(false);
            return new Success();
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            // The exception type only: its text can echo the connection string.
            return new PortError($"storage.prepare: migrations or indexes failed ({e.GetType().Name}).");
        }
    }
}
