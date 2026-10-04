using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Rewards;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Points;

/// <summary>
/// <see cref="ForReadingPlannedWork"/> on the <c>occurrences</c> and <c>tasks</c> collections of the Node server (port of <c>findPlannedOccurrences</c>):
/// the occurrences planned in a range, by <c>plannedDate</c>, that were not recorded as done, and the tasks of those without a points snapshot.
/// A read only; the rows are mapped by hand so the driver types stay in this class.
/// </summary>
internal sealed class MongoPlannedWorkReader : ForReadingPlannedWork
{
    private readonly IMongoCollection<BsonDocument> occurrences;
    private readonly IMongoCollection<BsonDocument> tasks;

    public MongoPlannedWorkReader(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        var database = MongoClientFactory.GetDatabase(client, options);
        occurrences = database.GetCollection<BsonDocument>(MongoCollections.Occurrences);
        tasks = database.GetCollection<BsonDocument>(MongoCollections.Tasks);
    }

    public async Task<OneOf<IReadOnlyList<PlannedWork>, PortError>> FindPlannedAsync(DateTimeOffset from, DateTimeOffset toExclusive, CancellationToken cancellationToken)
    {
        try
        {
            var filter = new BsonDocument
            {
                { "plannedDate", new BsonDocument { { "$gte", from.UtcDateTime }, { "$lt", toExclusive.UtcDateTime } } },
                { "recordedDone", new BsonDocument("$ne", true) },
            };
            var documents = await occurrences
                .Find(filter)
                .Project(new BsonDocument
                {
                    { "status", 1 }, { "plannedDate", 1 }, { "date", 1 }, { "recordedDone", 1 }, { "assigneeId", 1 }, { "periodOwnerId", 1 },
                    { "completedBy", 1 }, { "completedAt", 1 }, { "taskId", 1 }, { "durationMinutesSnapshot", 1 }, { "pointsSnapshot", 1 },
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Only the tasks of work without a snapshot are read, and each once.
            var needed = documents
                .Where(d => Number(d, "pointsSnapshot") is null && d.TryGetValue("taskId", out var task) && task.IsObjectId)
                .Select(d => d["taskId"].AsObjectId)
                .Distinct()
                .ToList();
            var values = new Dictionary<string, TaskPointValue>(StringComparer.Ordinal);
            if (needed.Count > 0)
            {
                var found = await tasks
                    .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(needed))))
                    .Project(new BsonDocument { { "points", 1 }, { "durationMinutes", 1 } })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var task in found)
                {
                    var id = ObjectIdConverter.ToHex(task["_id"].AsObjectId);
                    values[id] = new TaskPointValue(id, Number(task, "points"), Number(task, "durationMinutes") ?? 1);
                }
            }

            return OneOf<IReadOnlyList<PlannedWork>, PortError>.FromT0([.. documents.Select(d => ToPlannedWork(d, values))]);
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            return new PortError($"points.failed: could not read the planned work ({e.GetType().Name}).");
        }
    }

    private static PlannedWork ToPlannedWork(BsonDocument document, Dictionary<string, TaskPointValue> tasksById)
    {
        TaskPointValue? task = null;
        if (document.TryGetValue("taskId", out var taskId) && taskId.IsObjectId)
        {
            tasksById.TryGetValue(ObjectIdConverter.ToHex(taskId.AsObjectId), out task);
        }

        return new PlannedWork(
            MongoPointsBackfillStore.ToBonusSource(document),
            Number(document, "pointsSnapshot"),
            Number(document, "durationMinutesSnapshot") ?? 1,
            task);
    }

    private static int? Number(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsNumeric ? value.ToInt32() : null;
}
