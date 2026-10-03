using Huishoudplanner.Adapters.Mongo.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// Runs the registered migrations in order and records each applied one in the <c>migrations</c> collection
/// (<c>{ name, appliedAt }</c>). A recorded migration is skipped; a migration is recorded only after it succeeded,
/// so a failure leaves it unrecorded, stops the run (later migrations do not run) and surfaces the exception.
/// <para>
/// Concurrency: a unique index on <c>name</c> guarantees one record per migration. Two runners that start at the
/// same time can both apply a migration that is not recorded yet; that is safe because every migration is
/// idempotent, and the runner that records second treats the duplicate-key error as "already recorded".
/// Nothing is audited: a migration is a schema or snapshot repair, not a state change by a user.
/// </para>
/// </summary>
internal sealed class MigrationRunner
{
    private const int DuplicateKey = 11000;
    private const string NameIndex = "migrations_name_unique";

    private readonly IMongoDatabase database;
    private readonly TimeProvider timeProvider;
    private readonly IReadOnlyList<IMigration> migrations;

    public MigrationRunner(IMongoDatabase database, TimeProvider timeProvider, IReadOnlyList<IMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(migrations);
        var duplicate = migrations.GroupBy(m => m.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Migration name '{duplicate.Key}' is registered twice.", nameof(migrations));
        }

        this.database = database;
        this.timeProvider = timeProvider;
        this.migrations = migrations;
    }

    /// <summary>The registered names, in the order they run.</summary>
    public IReadOnlyList<string> MigrationNames => [.. migrations.Select(m => m.Name)];

    /// <summary>The shipped migrations: the two that exist in the Node server, in order.</summary>
    public static MigrationRunner CreateDefault(IMongoDatabase database, TimeProvider timeProvider) =>
        new(
            database,
            timeProvider,
            [
                new DropLegacyGeneratedSlotIndexMigration(new MongoIndexStore(database)),
                new BackfillOccurrenceRoomSnapshotsMigration(database),
            ]);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var recorded = database.GetCollection<BsonDocument>(MongoCollections.Migrations);
        await EnsureCollectionAsync(recorded, cancellationToken);

        var applied = (await recorded.Find(FilterDefinition<BsonDocument>.Empty)
                .Project(Builders<BsonDocument>.Projection.Include("name"))
                .ToListAsync(cancellationToken))
            .Select(d => d["name"].AsString)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var migration in migrations.Where(m => !applied.Contains(m.Name)))
        {
            await migration.ApplyAsync(cancellationToken);
            await RecordAsync(recorded, migration.Name, cancellationToken);
        }
    }

    private static async Task EnsureCollectionAsync(IMongoCollection<BsonDocument> recorded, CancellationToken cancellationToken)
    {
        // Creating the index creates the collection when it is missing, and is a no-op when it exists.
        var model = new CreateIndexModel<BsonDocument>(
            new BsonDocument("name", 1),
            new CreateIndexOptions { Name = NameIndex, Unique = true });
        await recorded.Indexes.CreateOneAsync(model, cancellationToken: cancellationToken);
    }

    private async Task RecordAsync(IMongoCollection<BsonDocument> recorded, string name, CancellationToken cancellationToken)
    {
        var document = new BsonDocument { { "name", name }, { "appliedAt", timeProvider.GetUtcNow().UtcDateTime } };
        try
        {
            await recorded.InsertOneAsync(document, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException e) when (e.WriteError.Code == DuplicateKey)
        {
            // A concurrent runner recorded it first; the goal is reached.
        }
    }
}
