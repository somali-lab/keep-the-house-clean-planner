using AwesomeAssertions;
using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// The audit writer (ADR-0004, ADR-0021) against a real single-node replica set: the entry commits or rolls back with
/// the entity write, never stands alone, and has the document shape of the Node server's <c>AuditEntryDoc</c>.
/// </summary>
public sealed class MongoAuditRecorderTests(MongoContainerFixture mongo) : IAsyncLifetime
{
    private const string ActorId = "0123456789abcdef01234567";
    private const string EntityId = "aaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly DateTimeOffset Now = new(2026, 10, 3, 8, 30, 15, 123, TimeSpan.Zero);

    private readonly MongoOptions options = new()
    {
        ConnectionString = mongo.ConnectionString,
        DatabaseName = MongoContainerFixture.NewDatabaseName(),
    };

    private IMongoClient client = null!;
    private IMongoCollection<BsonDocument> entities = null!;
    private IMongoCollection<BsonDocument> audit = null!;
    private MongoAuditRecorder recorder = null!;
    private MongoTransactionRunner runner = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public async ValueTask InitializeAsync()
    {
        client = MongoClientFactory.CreateClient(options);
        var database = MongoClientFactory.GetDatabase(client, options);
        await database.CreateCollectionAsync("entities", cancellationToken: Ct);
        await database.CreateCollectionAsync(MongoCollections.AuditLog, cancellationToken: Ct);
        entities = database.GetCollection<BsonDocument>("entities");
        audit = database.GetCollection<BsonDocument>(MongoCollections.AuditLog);
        var time = new FixedTimeProvider(Now);
        recorder = new MongoAuditRecorder(client, options, time);
        runner = new MongoTransactionRunner(client, time);
    }

    public async ValueTask DisposeAsync()
    {
        await client.DropDatabaseAsync(options.DatabaseName, Ct);
        client.Dispose();
    }

    private static AuditEntry Entry(AuditObject? meta = null) => new(
        new AuditActor(ActorId, AuditSource.Ui),
        AuditEntity.Room,
        EntityId,
        AuditAction.Update,
        AuditObject.Of(("name", "Zolder")),
        AuditObject.Of(("name", "Vliering")),
        meta);

    /// <summary>What a store does: write the entity in the ambient transaction.</summary>
    private Task InsertEntity(CancellationToken ct) =>
        entities.InsertOneAsync(MongoTransactionContext.Session!, new BsonDocument("_id", EntityId), cancellationToken: ct);

    private async Task<(long Entities, long Audit)> Counts() =>
    (
        await entities.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct),
        await audit.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)
    );

    [Fact]
    public async Task Record_InsideACommittedTransaction_MakesEntityAndEntryVisibleTogether()
    {
        var result = await runner.RunAsync(async ct =>
        {
            await InsertEntity(ct);
            var recorded = await recorder.RecordAsync(Entry(), ct);
            return TransactionOutcome.Commit(recorded);
        }, Ct);

        result.AsT0.IsT0.Should().BeTrue();
        (await Counts()).Should().Be((1, 1));
    }

    [Fact]
    public async Task Record_IsInvisibleOutsideUntilTheTransactionCommits()
    {
        var recordedInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = runner.RunAsync(async ct =>
        {
            await InsertEntity(ct);
            await recorder.RecordAsync(Entry(), ct);
            recordedInside.SetResult();
            await release.Task.WaitAsync(ct);
            return TransactionOutcome.Commit("done");
        }, Ct);

        await recordedInside.Task.WaitAsync(Ct);
        (await Counts()).Should().Be((0, 0));
        release.SetResult();
        await running;

        (await Counts()).Should().Be((1, 1));
    }

    [Fact]
    public async Task Record_WhenTheWorkAborts_LeavesNeitherEntityNorEntry()
    {
        var result = await runner.RunAsync<string>(async ct =>
        {
            await InsertEntity(ct);
            await recorder.RecordAsync(Entry(), ct);
            return TransactionOutcome.Abort("refused");
        }, Ct);

        result.AsT0.Should().Be("refused");
        (await Counts()).Should().Be((0, 0));
    }

    [Fact]
    public async Task Record_WhenTheWorkThrows_LeavesNeitherEntityNorEntry()
    {
        var act = () => runner.RunAsync<string>(async ct =>
        {
            await InsertEntity(ct);
            await recorder.RecordAsync(Entry(), ct);
            throw new InvalidOperationException("boom");
        }, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await Counts()).Should().Be((0, 0));
    }

    [Fact]
    public async Task Record_WhenTheEntryIsRejected_TheUseCaseCanAbortTheEntityWrite()
    {
        var result = await runner.RunAsync(async ct =>
        {
            await InsertEntity(ct);
            var recorded = await recorder.RecordAsync(Entry() with { EntityId = "not-an-id" }, ct);
            return recorded.IsT0 ? TransactionOutcome.Commit(recorded) : TransactionOutcome.Abort(recorded);
        }, Ct);

        result.AsT0.AsT1.Message.Should().StartWith("audit.invalid_entry");
        (await Counts()).Should().Be((0, 0));
    }

    [Fact]
    public async Task Record_OutsideATransaction_WritesNothingAndReturnsAPortError()
    {
        var result = await recorder.RecordAsync(Entry(), Ct);

        result.AsT1.Message.Should().StartWith("audit.no_transaction");
        (await Counts()).Audit.Should().Be(0);
    }

    [Fact]
    public async Task Record_AfterTheTransactionEnded_IsRejectedToo()
    {
        Task<PortError?>? leaked = null;
        await runner.RunAsync(ct =>
        {
            leaked = Task.Run(async () =>
            {
                await Task.Delay(50, ct);
                var late = await recorder.RecordAsync(Entry(), Ct);
                return late.IsT1 ? late.AsT1 : null;
            }, ct);
            return Task.FromResult(TransactionOutcome.Commit("done"));
        }, Ct);

        (await leaked!)!.Message.Should().StartWith("audit.no_transaction");
        (await Counts()).Audit.Should().Be(0);
    }

    [Fact]
    public async Task Record_WritesTheDocumentShapeOfTheNodeServer()
    {
        await runner.RunAsync(async ct =>
        {
            var meta = AuditObject.Of(("reason", "test"), ("count", 3));
            await recorder.RecordAsync(Entry(meta), ct);
            return TransactionOutcome.Commit("done");
        }, Ct);

        var document = await audit.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct);

        // Field names and order of AuditEntryDoc / record.ts in apps/server: _id, at, actorId, entity, entityId, action, before, after, source, meta.
        document.Names.Should().Equal("_id", "at", "actorId", "entity", "entityId", "action", "before", "after", "source", "meta");
        document["_id"].BsonType.Should().Be(BsonType.ObjectId);
        document["_id"].AsObjectId.CreationTime.Should().Be(Now.UtcDateTime.AddMilliseconds(-Now.Millisecond));
        document.Remove("_id");
        document.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson })
            .Should().Be(
                """
                { "at" : { "$date" : { "$numberLong" : "1791016215123" } }, "actorId" : { "$oid" : "0123456789abcdef01234567" }, "entity" : "room", "entityId" : { "$oid" : "aaaaaaaaaaaaaaaaaaaaaaaa" }, "action" : "update", "before" : { "name" : "Zolder" }, "after" : { "name" : "Vliering" }, "source" : "ui", "meta" : { "reason" : "test", "count" : { "$numberInt" : "3" } } }
                """.Trim());
    }

    [Fact]
    public async Task Record_WithoutMeta_OmitsTheMetaField()
    {
        await runner.RunAsync(async ct =>
        {
            await recorder.RecordAsync(Entry(), ct);
            return TransactionOutcome.Commit("done");
        }, Ct);

        var document = await audit.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct);

        document.Contains("meta").Should().BeFalse();
    }

    [Fact]
    public async Task Record_StoresValuesWithTheBsonTypesTheNodeServerWrites()
    {
        var before = AuditObject.Of(
            ("room", new AuditObjectId("bbbbbbbbbbbbbbbbbbbbbbbb")),
            ("gone", AuditNull.Instance));
        var after = AuditObject.Of(
            ("when", DateTimeOffset.Parse("2026-09-14T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            ("big", 5_000_000_000L),
            ("ratio", 1.5),
            ("tags", AuditArray.Of("a", true, 7)));

        await runner.RunAsync(async ct =>
        {
            await recorder.RecordAsync(Entry() with { Before = before, After = after }, ct);
            return TransactionOutcome.Commit("done");
        }, Ct);

        var document = await audit.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct);
        var b = document["before"].AsBsonDocument;
        var a = document["after"].AsBsonDocument;
        b["room"].BsonType.Should().Be(BsonType.ObjectId);
        b["gone"].BsonType.Should().Be(BsonType.Null);
        a["when"].BsonType.Should().Be(BsonType.DateTime);
        a["big"].BsonType.Should().Be(BsonType.Int64);
        a["ratio"].BsonType.Should().Be(BsonType.Double);
        a["tags"].AsBsonArray.Select(x => x.BsonType).Should().Equal(BsonType.String, BsonType.Boolean, BsonType.Int32);
    }

    [Fact]
    public async Task Record_StoresEverySourceAndTheSystemActor()
    {
        await runner.RunAsync(async ct =>
        {
            foreach (var source in Enum.GetValues<AuditSource>())
            {
                await recorder.RecordAsync(Entry() with { Actor = new AuditActor(AuditActor.SystemActorId, source) }, ct);
            }

            return TransactionOutcome.Commit("done");
        }, Ct);

        var sources = await audit.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);

        sources.Select(d => d["source"].AsString).Should().BeEquivalentTo("ui", "api", "ai", "system");
        sources.Should().OnlyContain(d => d["actorId"].AsObjectId == ObjectId.Empty);
    }

    [Fact]
    public async Task Record_WorksThroughTheEntryIndexesOfTheCatalog()
    {
        await runner.RunAsync(async ct =>
        {
            await recorder.RecordAsync(Entry(), ct);
            return TransactionOutcome.Commit("done");
        }, Ct);

        var found = await audit
            .Find(Builders<BsonDocument>.Filter.Eq("entity", "room") & Builders<BsonDocument>.Filter.Eq("entityId", ObjectId.Parse(EntityId)))
            .CountDocumentsAsync(Ct);

        found.Should().Be(1);
    }

    [Fact]
    public void AddMongoAdapter_RegistersTheAuditRecorder()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddMongoAdapter(_ => mongo.ConnectionString, options.DatabaseName);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ForRecordingAudit>().Should().BeOfType<MongoAuditRecorder>();
    }
}
