// Integration test template: Mongo driven-adapter tests with Testcontainers.MongoDb.
// Framework: xunit.v3 + AwesomeAssertions.
// The whole test assembly shares ONE mongo:8 single-node replica set (an assembly fixture,
// MongoDbBuilder().WithReplicaSet("rs0"), data on tmpfs). Each test class carves out its OWN
// uniquely named database via MongoContainerFixture.CreateDatabase, so classes stay isolated
// without paying for a container per class. Transactions only exist on a replica set, so the
// rollback of entity plus audit is tested for real (D10, D12).
// Illustrative: it need not compile, but the shapes are the rules.

using AwesomeAssertions;
using MongoDB.Driver;
using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Integration.Tests.Fixtures;

namespace Huishoudplanner.Integration.Tests.Persistence;

// The fixture is injected by constructor (xunit.v3 assembly fixture).
public sealed class StoreOccurrencesInMongoTemplateTests(MongoContainerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _database = null!;
    private StoreOccurrencesInMongo _sut = null!;
    private MongoTransactionRunner _transactions = null!;

    // IAsyncLifetime in xunit.v3 returns ValueTask (NOT Task).
    public async ValueTask InitializeAsync()
    {
        _database = mongo.CreateDatabase("occurrences");
        // Indexes and class maps come from the same code path as production.
        await MongoTestSetup.EnsureIndexesAsync(_database, TestContext.Current.CancellationToken);
        (_sut, _transactions) = MongoTestSetup.CreateOccurrenceStore(_database);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Replace_InsideTransaction_StoresEntityAndAuditEntryTogether()
    {
        // Arrange: fixtures are created through the adapter, never by a developer's leftovers.
        var ct = TestContext.Current.CancellationToken;
        var occurrence = await MongoTestSetup.SeedOpenOccurrenceAsync(_sut, ct);
        var completed = occurrence.Complete(TestActors.Planner.ActorId, MongoTestSetup.FixedNow);

        // Act
        var result = await _transactions.Run(
            async innerCt => (await _sut.Replace(completed, TestAudit.For(completed), innerCt)).Match<OneOf.OneOf<Success, PortError>>(
                ok => ok, nf => new PortError("missing"), c => new PortError("conflict"), pe => pe),
            ct);

        // Assert: success arm, both documents present.
        result.IsT0.Should().BeTrue();
        (await MongoTestSetup.CountAuditEntriesAsync(_database, occurrence.Id, ct)).Should().Be(1);
    }

    [Fact]
    public async Task Replace_WhenTransactionAborts_LeavesNeitherEntityNorAuditEntry()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var occurrence = await MongoTestSetup.SeedOpenOccurrenceAsync(_sut, ct);
        var completed = occurrence.Complete(TestActors.Planner.ActorId, MongoTestSetup.FixedNow);

        // Act: the work throws after the entity write, so the runner aborts the transaction and
        // turns the infrastructure failure into a PortError value.
        var result = await _transactions.Run<Success>(
            async innerCt =>
            {
                await _sut.Replace(completed, TestAudit.For(completed), innerCt);
                throw new MongoTestAbortException();
            },
            ct);

        // Assert: PortError arm, and the rollback is complete: still open, no audit entry.
        result.IsT1.Should().BeTrue();
        (await _sut.GetById(occurrence.Id, ct)).AsT0.Status.Should().Be(OccurrenceStatus.Open);
        (await MongoTestSetup.CountAuditEntriesAsync(_database, occurrence.Id, ct)).Should().Be(0);
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNotFound()
    {
        // Act: an ObjectId that exists nowhere; 24-hex in the API, ObjectId in storage (D11).
        var ct = TestContext.Current.CancellationToken;
        var result = await _sut.GetById(TestIds.Unknown, ct);

        // Assert: NotFound is a value, not an exception.
        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task List_WithLimit_ReturnsBoundedPageAndCursor()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await MongoTestSetup.SeedOpenOccurrencesAsync(_sut, count: 3, ct);

        // Act: lists are always bounded, with limit and cursor.
        var page = await _sut.List(new OccurrenceQuery(), limit: 2, cursor: null, ct);

        // Assert
        page.IsT0.Should().BeTrue();
        page.AsT0.Items.Should().HaveCount(2);
        page.AsT0.NextCursor.Should().NotBeNull();
    }
}
