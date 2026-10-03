using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Adapters.Mongo.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Servers;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Clusters;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// Error handling of the legacy slot index drop migration, with a fake store (the seam) instead of a server; needs no container.
/// Ports the "dropping the legacy slot index while another startup does the same" cases of health.test.ts.
/// </summary>
public sealed class DropLegacySlotIndexMigrationTests
{
    private static MongoCommandException ServerError(int code, string message) =>
        new(
            new ConnectionId(new ServerId(new ClusterId(1), new System.Net.DnsEndPoint("localhost", 27017))),
            message,
            new BsonDocument("dropIndexes", "occurrences"),
            new BsonDocument { { "ok", 0 }, { "code", code }, { "errmsg", message } });

    [Fact]
    public async Task Ignores_IndexNotFound_27_because_the_index_is_already_gone()
    {
        var store = new FakeStore(dropError: ServerError(27, "index not found"));

        await new DropLegacyGeneratedSlotIndexMigration(store).ApplyAsync(TestContext.Current.CancellationToken);

        store.DropAttempts.Should().Be(1);
        store.CreatedIndexCollections.Should().BeEmpty();
    }

    [Fact]
    public async Task Still_fails_on_any_other_error()
    {
        var store = new FakeStore(dropError: ServerError(13, "not authorized"));

        var ensure = async () => await new DropLegacyGeneratedSlotIndexMigration(store).ApplyAsync(TestContext.Current.CancellationToken);

        (await ensure.Should().ThrowAsync<MongoCommandException>()).WithMessage("*not authorized*");
    }

    [Fact]
    public async Task Does_not_drop_when_the_legacy_index_is_absent()
    {
        var store = new FakeStore(dropError: null, legacyIndexPresent: false);

        await new DropLegacyGeneratedSlotIndexMigration(store).ApplyAsync(TestContext.Current.CancellationToken);

        store.DropAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Does_not_drop_when_the_occurrences_collection_does_not_exist()
    {
        var store = new FakeStore(dropError: null, occurrencesExist: false);

        await new DropLegacyGeneratedSlotIndexMigration(store).ApplyAsync(TestContext.Current.CancellationToken);

        store.DropAttempts.Should().Be(0);
        store.ListedIndexCollections.Should().BeEmpty();
        store.CreatedCollections.Should().BeEmpty();
    }

    [Fact]
    public async Task Never_drops_the_partial_generated_slot_index_itself()
    {
        var store = new FakeStore(dropError: null, legacyIndexName: IndexCatalog.GeneratedSlotIndex);

        await new DropLegacyGeneratedSlotIndexMigration(store).ApplyAsync(TestContext.Current.CancellationToken);

        store.DropAttempts.Should().Be(0);
    }

    private sealed class FakeStore(
        Exception? dropError,
        bool legacyIndexPresent = true,
        bool occurrencesExist = true,
        string legacyIndexName = "cycleId_1_taskId_1_plannedDate_1") : IIndexStore
    {
        public int DropAttempts { get; private set; }

        public List<string> CreatedCollections { get; } = [];

        public List<string> CreatedIndexCollections { get; } = [];

        public List<string> ListedIndexCollections { get; } = [];

        public Task<bool> CollectionExistsAsync(string collection, CancellationToken cancellationToken) =>
            Task.FromResult(collection != MongoCollections.Occurrences || occurrencesExist);

        public Task CreateCollectionAsync(string collection, CancellationToken cancellationToken)
        {
            CreatedCollections.Add(collection);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExistingIndex>> ListIndexesAsync(string collection, CancellationToken cancellationToken)
        {
            ListedIndexCollections.Add(collection);
            IReadOnlyList<ExistingIndex> result = legacyIndexPresent
                ? [new ExistingIndex(legacyIndexName, [("cycleId", 1), ("taskId", 1), ("plannedDate", 1)])]
                : [];
            return Task.FromResult(result);
        }

        public Task DropIndexAsync(string collection, string indexName, CancellationToken cancellationToken)
        {
            DropAttempts++;
            return dropError is null ? Task.CompletedTask : Task.FromException(dropError);
        }

        public Task CreateIndexesAsync(string collection, IReadOnlyList<IndexSpec> indexes, CancellationToken cancellationToken)
        {
            CreatedIndexCollections.Add(collection);
            return Task.CompletedTask;
        }
    }
}
