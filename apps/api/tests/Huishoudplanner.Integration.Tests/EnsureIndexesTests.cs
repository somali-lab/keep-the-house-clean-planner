using Huishoudplanner.Adapters.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>Ports the ensureIndexes cases of apps/server/test/health.test.ts.</summary>
public sealed class EnsureIndexesTests(MongoContainerFixture mongo) : IAsyncLifetime
{
    private readonly MongoOptions options = new()
    {
        ConnectionString = mongo.ConnectionString,
        DatabaseName = MongoContainerFixture.NewDatabaseName(),
    };

    private IMongoClient client = null!;
    private IMongoDatabase database = null!;

    public async ValueTask InitializeAsync()
    {
        client = MongoClientFactory.CreateClient(options);
        database = MongoClientFactory.GetDatabase(client, options);
        await new IndexEnsurer(database).EnsureAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await client.DropDatabaseAsync(options.DatabaseName, TestContext.Current.CancellationToken);
        client.Dispose();
    }

    private static string KeyOf(BsonDocument index) => string.Join(",", index["key"].AsBsonDocument.Select(e => $"{e.Name}:{e.Value.ToInt32()}"));

    private static async Task<List<BsonDocument>> IndexesOf(IMongoDatabase db, string collection) =>
        await (await db.GetCollection<BsonDocument>(collection).Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Creates_every_expected_index_with_name_keys_unique_and_partial_filter()
    {
        foreach (var (collection, expected) in IndexCatalog.All)
        {
            var actual = await IndexesOf(database, collection);
            foreach (var spec in expected)
            {
                var match = actual.SingleOrDefault(i => i["name"].AsString == spec.Name);
                match.Should().NotBeNull($"{collection} {spec.Name}");
                KeyOf(match!).Should().Be(string.Join(",", spec.Keys.Select(k => $"{k.Field}:{k.Direction}")));
                match!.GetValue("unique", false).ToBoolean().Should().Be(spec.Unique, $"{collection} {spec.Name} unique");
                match.GetValue("partialFilterExpression", BsonNull.Value).Should()
                    .Be((BsonValue?)spec.PartialFilter ?? BsonNull.Value, $"{collection} {spec.Name} partial");
            }

            // Every collection exists even without custom indexes (users, rooms, settings).
            actual.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task Lists_todays_default_index_names()
    {
        var names = (await IndexesOf(database, MongoCollections.Occurrences)).Select(i => i["name"].AsString).ToList();
        names.Should().BeEquivalentTo(
        [
            "_id_",
            "date_1_assigneeId_1",
            "status_1_date_1",
            "taskId_1_completedAt_-1",
            "completedBy_1_status_1",
            "assigneeId_1_status_1",
            "plannedDate_1",
            "occurrences_generated_slot_unique",
            "occurrences_request_id_unique",
        ]);
    }

    [Fact]
    public async Task Includes_the_idempotency_and_cycle_unique_indexes()
    {
        var occurrences = await IndexesOf(database, MongoCollections.Occurrences);
        var slot = occurrences.Single(i => i["name"].AsString == IndexCatalog.GeneratedSlotIndex);
        slot["unique"].ToBoolean().Should().BeTrue();
        slot["partialFilterExpression"].Should().Be(new BsonDocument("origin", "generated"));
        occurrences.Single(i => i["name"].AsString == "occurrences_request_id_unique")["partialFilterExpression"]
            .Should().Be(new BsonDocument("requestId", new BsonDocument("$type", "string")));
        (await IndexesOf(database, MongoCollections.Cycles))
            .Should().Contain(i => i.GetValue("unique", false).ToBoolean() && KeyOf(i) == "index:1");
    }

    [Fact]
    public async Task Is_idempotent()
    {
        var before = (await IndexesOf(database, MongoCollections.Occurrences)).Select(i => i["name"].AsString).Order().ToList();

        await new IndexEnsurer(database).EnsureAsync(TestContext.Current.CancellationToken);

        (await IndexesOf(database, MongoCollections.Occurrences)).Select(i => i["name"].AsString).Order().Should().Equal(before);
    }

    [Fact]
    public async Task Migrates_the_legacy_slot_index_to_the_partial_index_and_changes_nothing_on_a_second_run()
    {
        var legacy = client.GetDatabase($"{options.DatabaseName}_legacy");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var occurrences = legacy.GetCollection<BsonDocument>(MongoCollections.Occurrences);
            await legacy.CreateCollectionAsync(MongoCollections.Occurrences, cancellationToken: ct);
            await occurrences.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    new BsonDocument { { "cycleId", 1 }, { "taskId", 1 }, { "plannedDate", 1 } },
                    new CreateIndexOptions { Unique = true }),
                cancellationToken: ct);

            var cycleId = ObjectId.GenerateNewId();
            var taskId = ObjectId.GenerateNewId();
            BsonDocument Doc(string origin) => new()
            {
                { "_id", ObjectId.GenerateNewId() },
                { "cycleId", cycleId },
                { "taskId", taskId },
                { "plannedDate", new DateTime(2026, 9, 16, 22, 0, 0, DateTimeKind.Utc) },
                { "origin", origin },
            };

            await occurrences.InsertOneAsync(Doc("generated"), cancellationToken: ct);
            // The legacy index still rejects an ad-hoc occurrence on the same slot.
            var rejected = async () => await occurrences.InsertOneAsync(Doc("adhoc"), cancellationToken: ct);
            await rejected.Should().ThrowAsync<MongoWriteException>();

            // Slice 0.6b: the legacy drop is migration 001; migrations run before the ensurer at startup.
            await MigrationRunner.CreateDefault(legacy, TimeProvider.System).RunAsync(ct);
            await new IndexEnsurer(legacy).EnsureAsync(ct);
            async Task<List<string>> Names() => (await IndexesOf(legacy, MongoCollections.Occurrences)).Select(i => i["name"].AsString).Order().ToList();
            var migrated = await Names();
            migrated.Should().Contain(IndexCatalog.GeneratedSlotIndex).And.NotContain("cycleId_1_taskId_1_plannedDate_1");
            var slotIndexes = (await IndexesOf(legacy, MongoCollections.Occurrences)).Where(i => KeyOf(i) == "cycleId:1,taskId:1,plannedDate:1").ToList();
            slotIndexes.Should().ContainSingle();
            slotIndexes[0]["unique"].ToBoolean().Should().BeTrue();
            slotIndexes[0]["partialFilterExpression"].Should().Be(new BsonDocument("origin", "generated"));

            // Partial filter: ad-hoc occurrences may share the slot key, generated ones still may not.
            await occurrences.InsertOneAsync(Doc("adhoc"), cancellationToken: ct);
            await occurrences.InsertOneAsync(Doc("adhoc"), cancellationToken: ct);
            var duplicate = async () => await occurrences.InsertOneAsync(Doc("generated"), cancellationToken: ct);
            await duplicate.Should().ThrowAsync<MongoWriteException>();
            (await occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct)).Should().Be(3);

            await MigrationRunner.CreateDefault(legacy, TimeProvider.System).RunAsync(ct);
            await new IndexEnsurer(legacy).EnsureAsync(ct);
            (await Names()).Should().Equal(migrated);
        }
        finally
        {
            await client.DropDatabaseAsync(legacy.DatabaseNamespace.DatabaseName, TestContext.Current.CancellationToken);
        }
    }
}
