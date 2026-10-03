using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Adapters.Mongo.Migrations;

/// <summary>
/// 002: backfills the room snapshots on occurrences created before room history existed. Port of
/// backfillOccurrenceRoomSnapshots (apps/server/src/data/occurrences.ts): matches occurrences missing either
/// snapshot field, sets both from the task's current room (null when the task, one-off or room is gone) and
/// touches nothing else; <c>updatedAt</c> stays and nothing is audited. Occurrences that have both snapshots
/// (even null) are never matched.
/// </summary>
internal sealed class BackfillOccurrenceRoomSnapshotsMigration(IMongoDatabase database) : IMigration
{
    private const int BatchSize = 1000;

    public string Name => "002-backfill-occurrence-room-snapshots";

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var occurrences = database.GetCollection<BsonDocument>(MongoCollections.Occurrences);
        var missing = new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("roomIdSnapshot", new BsonDocument("$exists", false)),
            new BsonDocument("roomNameSnapshot", new BsonDocument("$exists", false)),
        });
        var docs = await occurrences.Find(missing)
            .Project(Builders<BsonDocument>.Projection.Include("taskId"))
            .ToListAsync(cancellationToken);
        if (docs.Count == 0)
        {
            return;
        }

        // A one-off task (taskId null) always has its snapshots written at creation and is not matched here.
        var taskIds = docs.Select(TaskIdOf).OfType<ObjectId>().Distinct().ToList();
        var tasks = await database.GetCollection<BsonDocument>(MongoCollections.Tasks)
            .Find(Builders<BsonDocument>.Filter.In("_id", taskIds))
            .Project(Builders<BsonDocument>.Projection.Include("roomId"))
            .ToListAsync(cancellationToken);
        var taskRoom = tasks
            .Where(t => t.GetValue("roomId", BsonNull.Value).IsObjectId)
            .ToDictionary(t => t["_id"].AsObjectId, t => t["roomId"].AsObjectId);

        var roomIds = taskRoom.Values.Distinct().ToList();
        var rooms = await database.GetCollection<BsonDocument>(MongoCollections.Rooms)
            .Find(Builders<BsonDocument>.Filter.In("_id", roomIds))
            .Project(Builders<BsonDocument>.Projection.Include("name"))
            .ToListAsync(cancellationToken);
        var roomName = rooms
            .Where(r => r.GetValue("name", BsonNull.Value).IsString)
            .ToDictionary(r => r["_id"].AsObjectId, r => r["name"].AsString);

        var writes = docs.Select(doc =>
        {
            var roomId = TaskIdOf(doc) is { } taskId && taskRoom.TryGetValue(taskId, out var found) ? found : (ObjectId?)null;
            BsonValue roomIdValue = roomId is { } rid ? rid : BsonNull.Value;
            BsonValue nameValue = roomId is { } id && roomName.TryGetValue(id, out var name) ? name : BsonNull.Value;
            return (WriteModel<BsonDocument>)new UpdateOneModel<BsonDocument>(
                new BsonDocument("_id", doc["_id"]),
                new BsonDocument("$set", new BsonDocument
                {
                    { "roomIdSnapshot", roomIdValue },
                    { "roomNameSnapshot", nameValue },
                }));
        });

        foreach (var batch in writes.Chunk(BatchSize))
        {
            await occurrences.BulkWriteAsync(batch, cancellationToken: cancellationToken);
        }
    }

    private static ObjectId? TaskIdOf(BsonDocument doc) =>
        doc.GetValue("taskId", BsonNull.Value) is { IsObjectId: true } value ? value.AsObjectId : null;
}
