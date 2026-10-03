using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// The ad-hoc inserts and the retract delete (slice 3.3) against a real MongoDB replica set: the document the Node server inserts, the unique
/// request key, the guard of the retract, and the rule that both only run inside a transaction.
/// </summary>
public sealed class MongoAdhocOccurrenceStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day = new(2026, 9, 15, 22, 0, 0, TimeSpan.Zero);

    private const string Task1 = "a00000000000000000000001";
    private const string Cycle = "c00000000000000000000001";
    private const string Room = "b00000000000000000000001";
    private const string Anna = "0000000000000000000000a1";
    private const string Key = "extra-execution-key-0001";

    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;
    private readonly ForStoringOccurrences store;
    private readonly ForRunningTransactions transactions;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public MongoAdhocOccurrenceStoreTests(MongoContainerFixture mongo)
    {
        mongoClient = new MongoClient(mongo.ConnectionString);
        factory = ApiFactory.ForMongo(mongo, databaseName).WithoutSeeding();
        using var client = factory.CreateClient();
        store = factory.Services.GetRequiredService<ForStoringOccurrences>();
        transactions = factory.Services.GetRequiredService<ForRunningTransactions>();
    }

    public void Dispose()
    {
        factory.Dispose();
        mongoClient.DropDatabase(databaseName);
        mongoClient.Dispose();
    }

    private IMongoCollection<BsonDocument> Occurrences => mongoClient.GetDatabase(databaseName).GetCollection<BsonDocument>("occurrences");

    private static NewAdhocOccurrence Extra(string? key = Key, bool done = false) =>
        new(Task1, Cycle, Day, Anna, done, done ? Now : null, 30, "Afwas", Room, "Keuken", key, done ? 30 : null, null, Now);

    private static NewAdhocOccurrence OneOff(string? key = null, int? points = null, bool done = false) =>
        new(null, Cycle, Day, done ? Anna : null, done, done ? Now : null, 45, "Gordijnen ophangen", null, null, key, done ? points ?? 45 : null, points, Now);

    private async Task<TResult> CommitAsync<TResult>(Func<CancellationToken, Task<TResult>> work)
    {
        var ran = await transactions.RunAsync(async ct => TransactionOutcome.Commit(await work(ct)), Ct);
        return ran.AsT0;
    }

    private async Task<TResult> AbortAsync<TResult>(Func<CancellationToken, Task<TResult>> work)
    {
        var ran = await transactions.RunAsync(async ct => TransactionOutcome.Abort(await work(ct)), Ct);
        return ran.AsT0;
    }

    // ---- insert

    [Fact]
    public async Task InsertAdhoc_outsideATransactionWritesNothing()
    {
        var result = await store.InsertAdhocAsync(Extra(), Ct);

        result.AsT2.Message.Should().StartWith("occurrences.no_transaction");
        (await Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task InsertAdhoc_aPlannedExtraIsTheDocumentTheNodeServerInserts()
    {
        var stored = (await CommitAsync(ct => store.InsertAdhocAsync(Extra(), ct))).AsT0;

        var document = await Occurrences.Find(new BsonDocument("_id", ObjectId.Parse(stored.Id))).SingleAsync(Ct);
        document["taskId"].Should().Be(ObjectId.Parse(Task1));
        document["cycleId"].Should().Be(ObjectId.Parse(Cycle));
        document["planId"].IsBsonNull.Should().BeTrue();
        document["date"].ToUniversalTime().Should().Be(Day.UtcDateTime);
        document["plannedDate"].ToUniversalTime().Should().Be(Day.UtcDateTime);
        document["assigneeId"].Should().Be(ObjectId.Parse(Anna));
        (document["status"].AsString, document["origin"].AsString, document["recordedDone"].AsBoolean).Should().Be(("open", "adhoc", false));
        document["requestId"].Should().Be(Key);
        foreach (var nullField in new[] { "statusBeforeCompletion", "completedAt", "completedBy", "skipReason" })
        {
            document[nullField].IsBsonNull.Should().BeTrue(nullField);
        }

        (document["taskNameSnapshot"].AsString, document["roomNameSnapshot"].AsString, document["durationMinutesSnapshot"].AsInt32).Should().Be(("Afwas", "Keuken", 30));
        document.Contains("pointsSnapshot").Should().BeFalse();
        document.Contains("pointsOverride").Should().BeFalse();
        document.Contains("periodOwnerId").Should().BeFalse();
        stored.Should().BeEquivalentTo(new { Id = stored.Id, Status = OccurrenceStatus.Open, Origin = OccurrenceOrigin.Adhoc, RecordedDone = false, RequestId = Key });
    }

    [Fact]
    public async Task InsertAdhoc_aRecordedOneOffWithChosenPointsStoresTheNullTaskAndBothPointsFields()
    {
        var stored = (await CommitAsync(ct => store.InsertAdhocAsync(OneOff(points: 7, done: true), ct))).AsT0;

        var document = await Occurrences.Find(new BsonDocument("_id", ObjectId.Parse(stored.Id))).SingleAsync(Ct);
        document["taskId"].IsBsonNull.Should().BeTrue();
        document["roomIdSnapshot"].IsBsonNull.Should().BeTrue();
        document["roomNameSnapshot"].IsBsonNull.Should().BeTrue();
        document["requestId"].IsBsonNull.Should().BeTrue();
        (document["status"].AsString, document["recordedDone"].AsBoolean).Should().Be(("done", true));
        document["completedBy"].Should().Be(ObjectId.Parse(Anna));
        document["completedAt"].ToUniversalTime().Should().Be(Now.UtcDateTime);
        (document["pointsOverride"].AsInt32, document["pointsSnapshot"].AsInt32).Should().Be((7, 7));
        (stored.CompletedBy, stored.TaskId, stored.PointsOverride).Should().Be((Anna, null, 7));
    }

    [Fact]
    public async Task InsertAdhoc_aRequestKeyThatIsStoredAlreadyIsRequestKeyTakenAndTheTransactionCanBeRolledBack()
    {
        (await CommitAsync(ct => store.InsertAdhocAsync(Extra(), ct))).IsT0.Should().BeTrue();

        var again = await AbortAsync(ct => store.InsertAdhocAsync(Extra(), ct));

        again.IsT1.Should().BeTrue();
        (await Occurrences.CountDocumentsAsync(new BsonDocument("requestId", Key), cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task InsertAdhoc_withoutAKeyAnyNumberOfRecordsCoexist()
    {
        (await CommitAsync(ct => store.InsertAdhocAsync(OneOff(), ct))).IsT0.Should().BeTrue();
        (await CommitAsync(ct => store.InsertAdhocAsync(OneOff(), ct))).IsT0.Should().BeTrue();

        (await Occurrences.CountDocumentsAsync(new BsonDocument("taskId", BsonNull.Value), cancellationToken: Ct)).Should().Be(2);
    }

    [Fact]
    public async Task InsertAdhoc_twoExtrasOfOneTaskOnOneDayCoexistNextToAGeneratedOne()
    {
        await Occurrences.InsertOneAsync(
            new BsonDocument
            {
                { "taskId", ObjectId.Parse(Task1) }, { "cycleId", ObjectId.Parse(Cycle) }, { "plannedDate", Day.UtcDateTime }, { "date", Day.UtcDateTime },
                { "status", "open" }, { "origin", "generated" }, { "taskNameSnapshot", "Afwas" },
            },
            cancellationToken: Ct);

        (await CommitAsync(ct => store.InsertAdhocAsync(Extra("extra-execution-key-0001"), ct))).IsT0.Should().BeTrue();
        (await CommitAsync(ct => store.InsertAdhocAsync(Extra("extra-execution-key-0002"), ct))).IsT0.Should().BeTrue();

        (await Occurrences.CountDocumentsAsync(new BsonDocument("taskId", ObjectId.Parse(Task1)), cancellationToken: Ct)).Should().Be(3);
    }

    // ---- find and count

    [Fact]
    public async Task FindByRequestId_findsTheRecordOrNotFound()
    {
        var stored = (await CommitAsync(ct => store.InsertAdhocAsync(Extra(), ct))).AsT0;

        (await store.FindByRequestIdAsync(Key, Ct)).AsT0.Id.Should().Be(stored.Id);
        (await store.FindByRequestIdAsync("extra-execution-key-9999", Ct)).IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task CountOpenOfTaskOn_countsOnlyOpenOccurrencesOfThatTaskOnThatDay()
    {
        await CommitAsync(ct => store.InsertAdhocAsync(Extra("extra-execution-key-0001"), ct));
        await CommitAsync(ct => store.InsertAdhocAsync(Extra("extra-execution-key-0002"), ct));
        await CommitAsync(ct => store.InsertAdhocAsync(Extra("extra-execution-key-0003", done: true), ct));
        await CommitAsync(ct => store.InsertAdhocAsync(OneOff(), ct));

        (await store.CountOpenOfTaskOnAsync(Task1, Day, Ct)).AsT0.Should().Be(2);
        (await store.CountOpenOfTaskOnAsync(Task1, Day.AddDays(1), Ct)).AsT0.Should().Be(0);
        (await store.CountOpenOfTaskOnAsync("a00000000000000000000009", Day, Ct)).AsT0.Should().Be(0);
        (await store.CountOpenOfTaskOnAsync("nope", Day, Ct)).AsT0.Should().Be(0);
    }

    // ---- retract

    [Fact]
    public async Task DeleteRecorded_outsideATransactionDeletesNothing()
    {
        var stored = (await CommitAsync(ct => store.InsertAdhocAsync(Extra(done: true), ct))).AsT0;

        var result = await store.DeleteRecordedAsync(stored.Id, Ct);

        result.AsT2.Message.Should().StartWith("occurrences.no_transaction");
        (await Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
    }

    [Fact]
    public async Task DeleteRecorded_removesRecordedWorkAndReturnsItAsItWasAndASecondDeleteIsNotFound()
    {
        var stored = (await CommitAsync(ct => store.InsertAdhocAsync(Extra(done: true), ct))).AsT0;

        var deleted = (await CommitAsync(ct => store.DeleteRecordedAsync(stored.Id, ct))).AsT0;

        (deleted.Id, deleted.Status, deleted.RecordedDone, deleted.TaskNameSnapshot).Should().Be((stored.Id, OccurrenceStatus.Done, true, "Afwas"));
        (await CommitAsync(ct => store.DeleteRecordedAsync(stored.Id, ct))).IsT1.Should().BeTrue();
        (await Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task DeleteRecorded_neverDeletesPlannedAdhocWorkAGeneratedOccurrenceOrAnUnknownId()
    {
        var planned = (await CommitAsync(ct => store.InsertAdhocAsync(Extra(), ct))).AsT0;
        var generated = ObjectId.GenerateNewId();
        await Occurrences.InsertOneAsync(
            new BsonDocument { { "_id", generated }, { "status", "done" }, { "origin", "generated" }, { "recordedDone", true }, { "taskNameSnapshot", "x" } },
            cancellationToken: Ct);

        (await CommitAsync(ct => store.DeleteRecordedAsync(planned.Id, ct))).IsT1.Should().BeTrue();
        (await CommitAsync(ct => store.DeleteRecordedAsync(generated.ToString(), ct))).IsT1.Should().BeTrue();
        (await CommitAsync(ct => store.DeleteRecordedAsync("0123456789abcdef01234567", ct))).IsT1.Should().BeTrue();
        (await CommitAsync(ct => store.DeleteRecordedAsync("nope", ct))).IsT1.Should().BeTrue();

        (await Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(2);
    }
}
