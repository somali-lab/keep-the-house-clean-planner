using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// Creates every collection and index at startup; idempotent. Port of ensureIndexes in apps/server/src/data/db.ts.
/// </summary>
internal sealed class IndexEnsurer
{
    /// <summary>MongoDB error code for dropping an index that no longer exists.</summary>
    private const int IndexNotFound = 27;

    private static readonly (string Field, int Direction)[] LegacySlotKey =
        [("cycleId", 1), ("taskId", 1), ("plannedDate", 1)];

    private readonly IIndexStore store;

    public IndexEnsurer(IMongoDatabase database)
        : this(new MongoIndexStore(database))
    {
    }

    internal IndexEnsurer(IIndexStore store) => this.store = store;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        await DropLegacySlotIndexesAsync(cancellationToken);
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

    /// <summary>
    /// ADR-0009: the slot key was unique for every occurrence; it is now unique for generated ones only.
    /// Drops any other index with exactly that key (including the legacy default name) so the partial
    /// index can be created. Only when the collection exists. A schema change, not audited.
    /// </summary>
    private async Task DropLegacySlotIndexesAsync(CancellationToken cancellationToken)
    {
        if (!await store.CollectionExistsAsync(MongoCollections.Occurrences, cancellationToken))
        {
            return;
        }

        foreach (var index in await store.ListIndexesAsync(MongoCollections.Occurrences, cancellationToken))
        {
            if (!index.Keys.SequenceEqual(LegacySlotKey) || index.Name == IndexCatalog.GeneratedSlotIndex)
            {
                continue;
            }

            try
            {
                await store.DropIndexAsync(MongoCollections.Occurrences, index.Name, cancellationToken);
            }
            catch (MongoCommandException e) when (e.Code == IndexNotFound)
            {
                // A concurrent startup dropped it first: the goal is reached.
            }
        }
    }
}
