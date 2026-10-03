using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// The occurrence store against a real MongoDB replica set: the idempotent bulk insert that the unique slot index enforces (<c>insertOccurrencesIdempotent</c>
/// of the Node server, here an upsert because a duplicate key aborts a transaction), documents of exactly the Node shape, the reads that
/// generation needs, and the rule that nothing is written outside a transaction.
/// </summary>
public sealed class MongoOccurrenceStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private const string Task1 = "a00000000000000000000001";
    private const string Task2 = "a00000000000000000000002";
    private const string CycleA = "c00000000000000000000001";
    private const string CycleB = "c00000000000000000000002";
    private const string Plan = "e00000000000000000000001";
    private const string Room = "b00000000000000000000001";
    private const string Anna = "0000000000000000000000a1";

    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly ApiFactory factory;
    private readonly ForStoringOccurrences store;
    private readonly ForRunningTransactions transactions;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public MongoOccurrenceStoreTests(MongoContainerFixture mongo)
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

    private static DateTimeOffset Day(int day) => new(2026, 9, day, 0, 0, 0, TimeSpan.Zero);

    private static NewGeneratedOccurrence Draft(string task = Task1, string cycle = CycleA, int day = 16, string? assignee = Anna, string name = "Badkamer") =>
        new(task, cycle, Plan, Day(day), assignee, 30, name, Room, "Badkamer (ruimte)", Now);

    private async Task<IReadOnlyList<Occurrence>> InsertAsync(params NewGeneratedOccurrence[] drafts)
    {
        var ran = await transactions.RunAsync(async ct => TransactionOutcome.Commit((await store.InsertGeneratedAsync(drafts, ct)).AsT0), Ct);
        return ran.AsT0;
    }

    [Fact]
    public async Task Writes_outsideATransaction_writeNothingAndReturnAPortError()
    {
        (await store.InsertGeneratedAsync([Draft()], Ct)).AsT1.Message.Should().StartWith("occurrences.no_transaction");
        (await store.DeleteAsync(["0123456789abcdef01234567"], Ct)).AsT1.Message.Should().StartWith("occurrences.no_transaction");
        (await store.UpdateUpcomingRoomSnapshotsAsync(Task1, Day(1), Room, "Keuken", Ct)).AsT1.Message.Should().StartWith("occurrences.no_transaction");
        (await Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task AGeneratedOccurrence_isStoredWithExactlyTheFieldsAndTypesOfTheNodeServer()
    {
        var inserted = await InsertAsync(Draft());

        var stored = inserted.Should().ContainSingle().Subject;
        var document = await Occurrences.Find(new BsonDocument("_id", ObjectId.Parse(stored.Id))).SingleAsync(Ct);
        document.Names.Should().BeEquivalentTo(
        [
            "_id", "taskId", "cycleId", "planId", "date", "plannedDate", "assigneeId", "status", "statusBeforeCompletion", "completedAt",
            "completedBy", "skipReason", "durationMinutesSnapshot", "taskNameSnapshot", "roomIdSnapshot", "roomNameSnapshot", "origin",
            "createdAt", "updatedAt",
        ]);
        document["taskId"].Should().Be(ObjectId.Parse(Task1));
        document["cycleId"].Should().Be(ObjectId.Parse(CycleA));
        document["planId"].Should().Be(ObjectId.Parse(Plan));
        document["assigneeId"].Should().Be(ObjectId.Parse(Anna));
        document["roomIdSnapshot"].Should().Be(ObjectId.Parse(Room));
        document["date"].IsValidDateTime.Should().BeTrue();
        document["date"].ToUniversalTime().Should().Be(Day(16).UtcDateTime);
        document["plannedDate"].Should().Be(document["date"]);
        document["status"].AsString.Should().Be("open");
        document["origin"].AsString.Should().Be("generated");
        document["statusBeforeCompletion"].IsBsonNull.Should().BeTrue();
        document["completedAt"].IsBsonNull.Should().BeTrue();
        document["completedBy"].IsBsonNull.Should().BeTrue();
        document["skipReason"].IsBsonNull.Should().BeTrue();
        document["durationMinutesSnapshot"].BsonType.Should().Be(BsonType.Int32);
        document["taskNameSnapshot"].AsString.Should().Be("Badkamer");
        document["roomNameSnapshot"].AsString.Should().Be("Badkamer (ruimte)");
        document["createdAt"].ToUniversalTime().Should().Be(Now.UtcDateTime);
        document["updatedAt"].ToUniversalTime().Should().Be(Now.UtcDateTime);
        stored.PlannedDate.Should().Be(stored.Date);
    }

    [Fact]
    public async Task AnUnassignedOccurrenceWithoutARoomName_storesExplicitNulls()
    {
        var inserted = await InsertAsync(Draft(assignee: null) with { RoomNameSnapshot = null });

        var document = await Occurrences.Find(new BsonDocument("_id", ObjectId.Parse(inserted.Single().Id))).SingleAsync(Ct);
        document["assigneeId"].IsBsonNull.Should().BeTrue();
        document["roomNameSnapshot"].IsBsonNull.Should().BeTrue();
    }

    [Fact]
    public async Task InsertingTheSameSlotAgain_insertsNothingAndLeavesTheStoredOccurrenceUntouched()
    {
        var first = await InsertAsync(Draft());
        var again = await InsertAsync(Draft(assignee: null, name: "Andere naam"));

        first.Should().HaveCount(1);
        again.Should().BeEmpty();
        var documents = await Occurrences.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);
        documents.Should().ContainSingle();
        documents[0]["taskNameSnapshot"].AsString.Should().Be("Badkamer");
        documents[0]["assigneeId"].Should().Be(ObjectId.Parse(Anna));
    }

    [Fact]
    public async Task ABatchWithNewAndExistingSlots_returnsOnlyTheNewOnesInTheOrderOfTheDrafts_andIsIdempotentInsideOneBatch()
    {
        await InsertAsync(Draft(day: 16));

        var inserted = await InsertAsync(Draft(day: 17), Draft(day: 16), Draft(day: 18), Draft(day: 18), Draft(task: Task2, day: 16));

        inserted.Select(o => (o.TaskId, o.PlannedDate.Day)).Should().Equal((Task1, 17), (Task1, 18), (Task2, 16));
        (await Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(4);
    }

    [Fact]
    public async Task TheSameTaskAndDayInAnotherCycle_isAnotherSlot()
    {
        await InsertAsync(Draft());

        var inserted = await InsertAsync(Draft(cycle: CycleB));

        inserted.Should().ContainSingle();
    }

    [Fact]
    public async Task AnAdHocOccurrenceOnTheSlotDay_neverOccupiesTheSlot()
    {
        await Occurrences.InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "taskId", ObjectId.Parse(Task1) }, { "cycleId", ObjectId.Parse(CycleA) }, { "planId", BsonNull.Value },
                { "date", new BsonDateTime(Day(16).UtcDateTime) }, { "plannedDate", new BsonDateTime(Day(16).UtcDateTime) }, { "origin", "adhoc" }, { "status", "open" },
                { "durationMinutesSnapshot", 30 }, { "taskNameSnapshot", "Badkamer" }, { "assigneeId", BsonNull.Value },
                { "createdAt", new BsonDateTime(Now.UtcDateTime) }, { "updatedAt", new BsonDateTime(Now.UtcDateTime) },
            },
            cancellationToken: Ct);

        var inserted = await InsertAsync(Draft());

        inserted.Should().ContainSingle();
        (await Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(2);
    }

    [Fact]
    public async Task TheUniqueSlotIndex_refusesASecondGeneratedOccurrenceForTheSameSlotInTheDatabase()
    {
        await InsertAsync(Draft());
        var duplicate = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "taskId", ObjectId.Parse(Task1) }, { "cycleId", ObjectId.Parse(CycleA) },
            { "plannedDate", new BsonDateTime(Day(16).UtcDateTime) }, { "origin", "generated" },
        };

        var act = () => Occurrences.InsertOneAsync(duplicate, cancellationToken: Ct);

        (await act.Should().ThrowAsync<MongoWriteException>()).Which.WriteError.Category.Should().Be(ServerErrorCategory.DuplicateKey);
    }

    [Fact]
    public async Task AnAbortedTransaction_leavesNoOccurrence()
    {
        var ran = await transactions.RunAsync(async ct =>
        {
            await store.InsertGeneratedAsync([Draft()], ct);
            return TransactionOutcome.Abort(true);
        }, Ct);

        ran.IsT0.Should().BeTrue();
        (await Occurrences.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task TheReads_areBoundedByTheRange_filterOnOriginStatusAndDraggedWork_andComeInDisplayOrder()
    {
        await InsertAsync(
            Draft(day: 15, name: "Z"), Draft(day: 16, name: "B"), Draft(day: 16, task: Task2, name: "A"), Draft(day: 17, name: "C"), Draft(day: 20, name: "Buiten"));
        var all = (await Occurrences.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct)).ToDictionary(d => d["taskNameSnapshot"].AsString);
        // a done one, a skipped one and a dragged one stay out of "replaceable" but not out of "generated"
        await Occurrences.UpdateOneAsync(new BsonDocument("_id", all["C"]["_id"]), new BsonDocument("$set", new BsonDocument("status", "done")), cancellationToken: Ct);
        await Occurrences.UpdateOneAsync(new BsonDocument("_id", all["Z"]["_id"]), new BsonDocument("$set", new BsonDocument("date", new BsonDateTime(Day(16).UtcDateTime))), cancellationToken: Ct);

        var generated = (await store.FindGeneratedPlannedBetweenAsync(Day(15), Day(20), Ct)).AsT0;
        var replaceable = (await store.FindReplaceableBetweenAsync(Day(15), Day(20), Ct)).AsT0;

        generated.Select(o => o.TaskNameSnapshot).Should().Equal("A", "B", "Z", "C"); // by the day it sits on (Z was dragged to the 16th), then name
        replaceable.Select(o => o.TaskNameSnapshot).Should().Equal("A", "B");
        replaceable.Should().OnlyContain(o => o.Status == OccurrenceStatus.Open && o.Date == o.PlannedDate);
    }

    [Fact]
    public async Task Delete_removesOnlyTheGivenOccurrences_andCountsThem()
    {
        var inserted = await InsertAsync(Draft(day: 16), Draft(day: 17), Draft(day: 18));

        var ran = await transactions.RunAsync(async ct => TransactionOutcome.Commit((await store.DeleteAsync([inserted[0].Id, inserted[2].Id, "not-an-id", "0123456789abcdef01234567"], ct)).AsT0), Ct);

        ran.AsT0.Should().Be(2);
        (await Occurrences.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct)).Select(d => d["_id"].AsObjectId.ToString()).Should().Equal(inserted[1].Id);
    }

    [Fact]
    public async Task RoomSnapshots_followOnlyOpenOccurrencesOfTheTaskFromTheGivenDay_withoutTouchingUpdatedAt()
    {
        var inserted = await InsertAsync(Draft(day: 14), Draft(day: 16), Draft(day: 17), Draft(task: Task2, day: 16));
        await Occurrences.UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(inserted[2].Id)), new BsonDocument("$set", new BsonDocument("status", "done")), cancellationToken: Ct);

        var ran = await transactions.RunAsync(async ct => TransactionOutcome.Commit((await store.UpdateUpcomingRoomSnapshotsAsync(Task1, Day(15), "b00000000000000000000002", "Keuken", ct)).AsT0), Ct);

        ran.AsT0.Should().Be(1);
        var byId = (await Occurrences.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct)).ToDictionary(d => d["_id"].AsObjectId.ToString());
        byId[inserted[1].Id]["roomNameSnapshot"].AsString.Should().Be("Keuken");
        byId[inserted[1].Id]["roomIdSnapshot"].Should().Be(ObjectId.Parse("b00000000000000000000002"));
        byId[inserted[1].Id]["updatedAt"].ToUniversalTime().Should().Be(Now.UtcDateTime);
        foreach (var untouched in new[] { inserted[0].Id, inserted[2].Id, inserted[3].Id })
        {
            byId[untouched]["roomNameSnapshot"].AsString.Should().Be("Badkamer (ruimte)");
        }
    }

    [Fact]
    public async Task ADocumentFromTheNodeServer_withMissingOptionalFieldsAndUnknownOnes_isRead()
    {
        var id = ObjectId.GenerateNewId();
        await Occurrences.InsertOneAsync(
            new BsonDocument
            {
                { "_id", id }, { "taskId", BsonNull.Value }, { "cycleId", ObjectId.Parse(CycleA) }, { "planId", BsonNull.Value },
                { "date", new BsonDateTime(Day(16).UtcDateTime) }, { "plannedDate", new BsonDateTime(Day(16).UtcDateTime) }, { "assigneeId", ObjectId.Parse(Anna) },
                { "status", "done" }, { "statusBeforeCompletion", "skipped" }, { "completedAt", new BsonDateTime(Now.UtcDateTime) }, { "completedBy", ObjectId.Parse(Anna) },
                { "skipReason", BsonNull.Value }, { "durationMinutesSnapshot", 20 }, { "taskNameSnapshot", "Eenmalig" }, { "origin", "adhoc" },
                { "recordedDone", true }, { "requestId", "request-key-123456" }, { "pointsSnapshot", 20 }, { "pointsOverride", 25 }, { "periodOwnerId", ObjectId.Parse(Anna) },
                { "somethingNew", "ignored" }, { "createdAt", new BsonDateTime(Now.UtcDateTime) }, { "updatedAt", new BsonDateTime(Now.UtcDateTime) },
            },
            cancellationToken: Ct);

        var found = (await store.FindGeneratedPlannedBetweenAsync(Day(1), Day(30), Ct)).AsT0;
        found.Should().BeEmpty("it is ad hoc");
        await Occurrences.UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument("$set", new BsonDocument("origin", "generated")), cancellationToken: Ct);

        var read = (await store.FindGeneratedPlannedBetweenAsync(Day(1), Day(30), Ct)).AsT0.Single();

        read.TaskId.Should().BeNull();
        read.Status.Should().Be(OccurrenceStatus.Done);
        read.StatusBeforeCompletion.Should().Be(OccurrenceStatus.Skipped);
        (read.RecordedDone, read.RequestId, read.PointsSnapshot, read.PointsOverride, read.PeriodOwnerId).Should().Be((true, "request-key-123456", 20, 25, Anna));
        read.RoomIdSnapshot.Should().BeNull("a missing optional field reads as null");
        read.RoomNameSnapshot.Should().BeNull();
    }
}
