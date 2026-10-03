using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// Creates every collection and index at startup; idempotent. Port of ensureIndexes in apps/server/src/data/db.ts.
/// Run <see cref="MigrationRunner"/> first: migration 001 drops the legacy slot index that would otherwise
/// conflict with the partial generated-slot index created here.
/// </summary>
internal sealed class IndexEnsurer
{
    private readonly IIndexStore store;

    public IndexEnsurer(IMongoDatabase database)
        : this(new MongoIndexStore(database))
    {
    }

    internal IndexEnsurer(IIndexStore store) => this.store = store;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (collection, indexes) in IndexCatalog.All)
        {
            if (!await store.CollectionExistsAsync(collection, cancellationToken))
            {
                await store.CreateCollectionAsync(collection, cancellationToken);
            }

            if (indexes.Count > 0)
            {
                await store.CreateIndexesAsync(collection, indexes, cancellationToken);
            }
        }
    }
}
