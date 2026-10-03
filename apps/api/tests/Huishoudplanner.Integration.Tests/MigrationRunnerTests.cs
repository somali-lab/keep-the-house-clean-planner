using Huishoudplanner.Adapters.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>The migration runner against a real database: order, recording, idempotence, failure and concurrency.</summary>
public sealed class MigrationRunnerTests(MongoContainerFixture mongo) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 8, 30, 0, TimeSpan.Zero);

    private readonly MongoOptions options = new()
    {
        ConnectionString = mongo.ConnectionString,
        DatabaseName = MongoContainerFixture.NewDatabaseName(),
    };

    private IMongoClient client = null!;
    private IMongoDatabase database = null!;

    public ValueTask InitializeAsync()
    {
        client = MongoClientFactory.CreateClient(options);
        database = MongoClientFactory.GetDatabase(client, options);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await client.DropDatabaseAsync(options.DatabaseName, TestContext.Current.CancellationToken);
        client.Dispose();
    }

    private MigrationRunner Runner(params IMigration[] migrations) => new(database, new FixedTimeProvider(Now), migrations);

    private IMongoCollection<BsonDocument> Recorded => database.GetCollection<BsonDocument>(MongoCollections.Migrations);

    private async Task<List<string>> RecordedNames() =>
        (await Recorded.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(TestContext.Current.CancellationToken))
            .Select(d => d["name"].AsString).Order().ToList();

    [Fact]
    public async Task Runs_the_migrations_in_order_and_records_each_with_its_name_and_applied_time()
    {
        var log = new List<string>();

        await Runner(new Step("001-a", log), new Step("002-b", log)).RunAsync(TestContext.Current.CancellationToken);

        log.Should().Equal("001-a", "002-b");
        var records = await Recorded.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(TestContext.Current.CancellationToken);
        records.Select(r => r["name"].AsString).Should().BeEquivalentTo(["001-a", "002-b"]);
        records.Should().OnlyContain(r => r["appliedAt"].ToUniversalTime() == Now.UtcDateTime);
    }

    [Fact]
    public async Task A_second_run_applies_nothing_and_records_nothing_new()
    {
        var log = new List<string>();
        var runner = Runner(new Step("001-a", log), new Step("002-b", log));
        await runner.RunAsync(TestContext.Current.CancellationToken);
        log.Clear();

        await runner.RunAsync(TestContext.Current.CancellationToken);

        log.Should().BeEmpty();
        (await Recorded.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: TestContext.Current.CancellationToken))
            .Should().Be(2);
    }

    [Fact]
    public async Task A_new_migration_added_later_is_the_only_one_applied()
    {
        var log = new List<string>();
        await Runner(new Step("001-a", log)).RunAsync(TestContext.Current.CancellationToken);
        log.Clear();

        await Runner(new Step("001-a", log), new Step("002-b", log)).RunAsync(TestContext.Current.CancellationToken);

        log.Should().Equal("002-b");
    }

    [Fact]
    public async Task Creates_the_migrations_collection_with_a_unique_index_on_name()
    {
        await Runner().RunAsync(TestContext.Current.CancellationToken);

        var indexes = await (await Recorded.Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);
        var onName = indexes.Single(i => i["key"].AsBsonDocument.Names.SequenceEqual(["name"]));
        onName["unique"].ToBoolean().Should().BeTrue();

        BsonDocument Record() => new() { { "name", "x" }, { "appliedAt", Now.UtcDateTime } };
        await Recorded.InsertOneAsync(Record(), cancellationToken: TestContext.Current.CancellationToken);
        var duplicate = async () => await Recorded.InsertOneAsync(Record(), cancellationToken: TestContext.Current.CancellationToken);
        await duplicate.Should().ThrowAsync<MongoWriteException>();
    }

    [Fact]
    public async Task A_failing_migration_stays_unrecorded_and_the_later_ones_do_not_run()
    {
        var log = new List<string>();
        var runner = Runner(new Step("001-a", log), new Step("002-b", log, fail: true), new Step("003-c", log));

        var run = async () => await runner.RunAsync(TestContext.Current.CancellationToken);

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*002-b*");
        log.Should().Equal("001-a", "002-b");
        (await RecordedNames()).Should().Equal("001-a");

        // After the cause is fixed, the next run continues where it stopped.
        log.Clear();
        await Runner(new Step("001-a", log), new Step("002-b", log), new Step("003-c", log)).RunAsync(TestContext.Current.CancellationToken);
        log.Should().Equal("002-b", "003-c");
        (await RecordedNames()).Should().Equal("001-a", "002-b", "003-c");
    }

    [Fact]
    public async Task Two_runners_at_the_same_time_leave_one_record_per_migration_and_neither_fails()
    {
        var gate = new TaskCompletionSource();
        var log = new List<string>();
        var slow = new Step("001-a", log, gate: gate.Task);
        var first = Runner(slow).RunAsync(TestContext.Current.CancellationToken);
        var second = Runner(slow).RunAsync(TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        gate.SetResult();

        await Task.WhenAll(first, second);

        (await RecordedNames()).Should().Equal("001-a");
    }

    [Fact]
    public void The_default_set_starts_with_the_two_existing_migrations_in_order()
    {
        MigrationRunner.CreateDefault(database, TimeProvider.System).MigrationNames.Should().Equal(
            "001-drop-legacy-generated-slot-index",
            "002-backfill-occurrence-room-snapshots");
    }

    [Fact]
    public async Task The_default_set_migrates_a_legacy_database_and_leaves_it_ready_for_the_index_ensurer()
    {
        var ct = TestContext.Current.CancellationToken;
        await database.CreateCollectionAsync(MongoCollections.Occurrences, cancellationToken: ct);
        await database.GetCollection<BsonDocument>(MongoCollections.Occurrences).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "cycleId", 1 }, { "taskId", 1 }, { "plannedDate", 1 } },
                new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);

        await MigrationRunner.CreateDefault(database, TimeProvider.System).RunAsync(ct);
        await new IndexEnsurer(database).EnsureAsync(ct);

        (await RecordedNames()).Should().Equal("001-drop-legacy-generated-slot-index", "002-backfill-occurrence-room-snapshots");
    }

    private sealed class Step(string name, List<string> log, bool fail = false, Task? gate = null) : IMigration
    {
        public string Name => name;

        public async Task ApplyAsync(CancellationToken cancellationToken)
        {
            lock (log)
            {
                log.Add(name);
            }

            if (gate is not null)
            {
                await gate;
            }

            if (fail)
            {
                throw new InvalidOperationException($"{name} failed");
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
