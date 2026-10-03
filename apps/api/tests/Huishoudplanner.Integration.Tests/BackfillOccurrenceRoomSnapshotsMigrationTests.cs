using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Adapters.Mongo.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// Ports the backfill case of apps/server/test/tasks.test.ts (backfillOccurrenceRoomSnapshots) and adds the
/// semantics that test leaves implicit: existing snapshots stay, nothing but the two snapshot fields changes.
/// </summary>
public sealed class BackfillOccurrenceRoomSnapshotsMigrationTests(MongoContainerFixture mongo) : IAsyncLifetime
{
    private static readonly DateTime Stamp = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

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

    private IMongoCollection<BsonDocument> Occurrences => database.GetCollection<BsonDocument>(MongoCollections.Occurrences);

    private async Task<ObjectId> Insert(string collection, BsonDocument document)
    {
        var id = ObjectId.GenerateNewId();
        document["_id"] = id;
        await database.GetCollection<BsonDocument>(collection).InsertOneAsync(document, cancellationToken: TestContext.Current.CancellationToken);
        return id;
    }

    private Task<ObjectId> InsertOccurrence(BsonValue taskId, params BsonElement[] extra)
    {
        var doc = new BsonDocument { { "taskId", taskId }, { "status", "open" }, { "createdAt", Stamp }, { "updatedAt", Stamp } };
        foreach (var element in extra)
        {
            doc[element.Name] = element.Value;
        }

        return Insert(MongoCollections.Occurrences, doc);
    }

    private async Task<BsonDocument> Get(ObjectId id) =>
        await Occurrences.Find(new BsonDocument("_id", id)).SingleAsync(TestContext.Current.CancellationToken);

    private Task Run() => new BackfillOccurrenceRoomSnapshotsMigration(database).ApplyAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Fills_the_room_id_and_name_from_the_task_and_its_room()
    {
        var roomId = await Insert(MongoCollections.Rooms, new BsonDocument("name", "Badkamer"));
        var taskId = await Insert(MongoCollections.Tasks, new BsonDocument("roomId", roomId));
        var first = await InsertOccurrence(taskId);
        var second = await InsertOccurrence(taskId, new BsonElement("status", "done"));

        await Run();

        foreach (var id in new[] { first, second })
        {
            var doc = await Get(id);
            doc["roomIdSnapshot"].AsObjectId.Should().Be(roomId);
            doc["roomNameSnapshot"].AsString.Should().Be("Badkamer");
        }
    }

    [Fact]
    public async Task Leaves_updatedAt_and_every_other_field_untouched()
    {
        var roomId = await Insert(MongoCollections.Rooms, new BsonDocument("name", "Badkamer"));
        var taskId = await Insert(MongoCollections.Tasks, new BsonDocument("roomId", roomId));
        var id = await InsertOccurrence(taskId, new BsonElement("taskNameSnapshot", "Schrobben"));
        var before = await Get(id);

        await Run();

        var after = await Get(id);
        after["updatedAt"].ToUniversalTime().Should().Be(Stamp);
        after.Remove("roomIdSnapshot");
        after.Remove("roomNameSnapshot");
        after.ToJson().Should().Be(before.ToJson());
    }

    [Fact]
    public async Task Leaves_occurrences_that_already_have_both_snapshots_alone_even_when_the_task_moved()
    {
        var oldRoom = await Insert(MongoCollections.Rooms, new BsonDocument("name", "Badkamer"));
        var newRoom = await Insert(MongoCollections.Rooms, new BsonDocument("name", "Keuken"));
        var taskId = await Insert(MongoCollections.Tasks, new BsonDocument("roomId", newRoom));
        var kept = await InsertOccurrence(taskId, new BsonElement("roomIdSnapshot", oldRoom), new BsonElement("roomNameSnapshot", "Badkamer"));
        var keptNull = await InsertOccurrence(taskId, new BsonElement("roomIdSnapshot", BsonNull.Value), new BsonElement("roomNameSnapshot", BsonNull.Value));
        var missing = await InsertOccurrence(taskId);

        await Run();

        (await Get(kept))["roomNameSnapshot"].AsString.Should().Be("Badkamer");
        (await Get(kept))["roomIdSnapshot"].AsObjectId.Should().Be(oldRoom);
        (await Get(keptNull))["roomIdSnapshot"].IsBsonNull.Should().BeTrue();
        (await Get(missing))["roomNameSnapshot"].AsString.Should().Be("Keuken");
    }

    [Fact]
    public async Task Writes_null_snapshots_for_a_one_off_task_a_deleted_task_and_a_task_whose_room_is_gone()
    {
        var goneRoom = ObjectId.GenerateNewId();
        var orphanTask = await Insert(MongoCollections.Tasks, new BsonDocument("roomId", goneRoom));
        var oneOff = await InsertOccurrence(BsonNull.Value);
        var deletedTask = await InsertOccurrence(ObjectId.GenerateNewId());
        var roomGone = await InsertOccurrence(orphanTask);

        await Run();

        foreach (var id in new[] { oneOff, deletedTask })
        {
            var doc = await Get(id);
            doc["roomIdSnapshot"].IsBsonNull.Should().BeTrue();
            doc["roomNameSnapshot"].IsBsonNull.Should().BeTrue();
        }

        var orphan = await Get(roomGone);
        orphan["roomIdSnapshot"].AsObjectId.Should().Be(goneRoom);
        orphan["roomNameSnapshot"].IsBsonNull.Should().BeTrue();
    }

    [Fact]
    public async Task Completes_an_occurrence_that_has_only_one_of_the_two_snapshots()
    {
        var roomId = await Insert(MongoCollections.Rooms, new BsonDocument("name", "Badkamer"));
        var taskId = await Insert(MongoCollections.Tasks, new BsonDocument("roomId", roomId));
        var id = await InsertOccurrence(taskId, new BsonElement("roomIdSnapshot", roomId));

        await Run();

        (await Get(id))["roomNameSnapshot"].AsString.Should().Be("Badkamer");
    }

    [Fact]
    public async Task Does_nothing_on_an_empty_database_and_on_a_second_run()
    {
        await Run();
        var roomId = await Insert(MongoCollections.Rooms, new BsonDocument("name", "Badkamer"));
        var taskId = await Insert(MongoCollections.Tasks, new BsonDocument("roomId", roomId));
        var id = await InsertOccurrence(taskId);
        await Run();
        var afterFirst = await Get(id);

        await Run();

        (await Get(id)).ToJson().Should().Be(afterFirst.ToJson());
    }
}
