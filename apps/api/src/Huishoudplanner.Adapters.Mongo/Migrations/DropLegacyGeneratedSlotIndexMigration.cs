using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Migrations;

/// <summary>
/// 001: ADR-0009 made the slot key unique for generated occurrences only; before, it was unique for every
/// occurrence. Drops any other index with exactly that key (including the legacy default name) so the partial
/// index can be created. Only when the collection exists. A schema change, not audited. Moved here from the
/// index ensurer (port of the drop in ensureIndexes, apps/server/src/data/db.ts).
/// </summary>
internal sealed class DropLegacyGeneratedSlotIndexMigration(IIndexStore store) : IMigration
{
    /// <summary>MongoDB error code for dropping an index that no longer exists.</summary>
    private const int IndexNotFound = 27;

    private static readonly (string Field, int Direction)[] LegacySlotKey =
        [("cycleId", 1), ("taskId", 1), ("plannedDate", 1)];

    public string Name => "001-drop-legacy-generated-slot-index";

    public async Task ApplyAsync(CancellationToken cancellationToken)
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
