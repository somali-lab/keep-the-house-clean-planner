using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Tasks;

/// <summary>
/// <see cref="ForStoringTasks"/> on the <c>tasks</c> collection. The documents are exactly the ones of the Node server
/// (<c>TaskDoc</c>: <c>_id, name, roomId, intervalKey, durationMinutes, points, defaultAssigneeId, active, notes, tags,
/// lastCompletedAt, createdAt, updatedAt</c>), so both applications read each other's tasks. Mapped by hand: the driver types stay
/// in this class. A document without <c>points</c> (from before ADR-0011) reads as the default for its duration.
/// </summary>
/// <remarks>
/// Writes enlist in the running transaction (<see cref="MongoTransactionContext.Session"/>) and refuse to run without one: a task change
/// without its audit entry must never exist. Reads join the transaction when there is one. A transient transaction error propagates
/// so the runner retries the attempt; any other infrastructure failure is a <see cref="PortError"/> without configuration values.
/// </remarks>
internal sealed class MongoTaskStore : ForStoringTasks
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> tasks;

    public MongoTaskStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        tasks = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Tasks);
    }

    public async Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> ListAsync(string? roomId, bool? active, TaskCursor? after, int take, CancellationToken cancellationToken)
    {
        var filters = new List<FilterDefinition<BsonDocument>>();
        if (roomId is not null)
        {
            if (!ObjectIdConverter.TryParse(roomId, out var room))
            {
                return OneOf<IReadOnlyList<HouseholdTask>, PortError>.FromT0([]);
            }

            filters.Add(new BsonDocument("roomId", room));
        }

        if (active is { } flag)
        {
            filters.Add(new BsonDocument("active", flag));
        }

        if (after is not null)
        {
            filters.Add(AfterFilter(after));
        }

        var filter = filters.Count == 0 ? FilterDefinition<BsonDocument>.Empty : Builders<BsonDocument>.Filter.And(filters);
        try
        {
            var documents = await FindFluent(filter).Sort(ListOrder).Limit(take).ToListAsync(cancellationToken).ConfigureAwait(false);
            return OneOf<IReadOnlyList<HouseholdTask>, PortError>.FromT0(documents.ConvertAll(ToTask));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("list", e);
        }
    }

    public async Task<OneOf<HouseholdTask, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(id, out var objectId))
        {
            return new NotFound();
        }

        try
        {
            var document = await FindFluent(new BsonDocument("_id", objectId)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null ? new NotFound() : ToTask(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("find", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> FindManyAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var objectIds = ids.Where(id => ObjectIdConverter.TryParse(id, out _)).Select(ObjectIdConverter.Parse).ToList();
        if (objectIds.Count == 0)
        {
            return OneOf<IReadOnlyList<HouseholdTask>, PortError>.FromT0([]);
        }

        try
        {
            var documents = await FindFluent(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(objectIds))))
                .Sort(ListOrder)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return OneOf<IReadOnlyList<HouseholdTask>, PortError>.FromT0(documents.ConvertAll(ToTask));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("find several", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> ListActiveInRoomAsync(string roomId, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(roomId, out var room))
        {
            return OneOf<IReadOnlyList<HouseholdTask>, PortError>.FromT0([]);
        }

        try
        {
            var documents = await FindFluent(new BsonDocument { { "roomId", room }, { "active", true } })
                .Sort(ListOrder)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return OneOf<IReadOnlyList<HouseholdTask>, PortError>.FromT0(documents.ConvertAll(ToTask));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("list the tasks of a room", e);
        }
    }

    public async Task<OneOf<HouseholdTask, PortError>> InsertAsync(NewTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        var now = new BsonDateTime(task.CreatedAt.UtcDateTime);
        var document = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "name", task.Name },
            { "roomId", ObjectIdConverter.Parse(task.RoomId) },
            { "intervalKey", task.IntervalKey },
            { "durationMinutes", task.DurationMinutes },
            { "points", task.Points },
            { "defaultAssigneeId", task.DefaultAssigneeId is { } assignee ? ObjectIdConverter.Parse(assignee) : BsonNull.Value },
            { "notes", task.Notes },
            { "tags", new BsonArray(task.Tags) },
            { "active", true },
            { "lastCompletedAt", BsonNull.Value },
            { "createdAt", now },
            { "updatedAt", now },
        };
        try
        {
            await tasks.InsertOneAsync(session, document, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ToTask(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("insert", e);
        }
    }

    public async Task<OneOf<HouseholdTask, NotFound, PortError>> UpdateAsync(string id, TaskChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (!ObjectIdConverter.TryParse(id, out var objectId))
        {
            return new NotFound();
        }

        var set = new BsonDocument();
        if (changes.Name is not null)
        {
            set.Add("name", changes.Name);
        }

        if (changes.RoomId is not null)
        {
            set.Add("roomId", ObjectIdConverter.Parse(changes.RoomId));
        }

        if (changes.IntervalKey is not null)
        {
            set.Add("intervalKey", changes.IntervalKey);
        }

        if (changes.DurationMinutes is { } minutes)
        {
            set.Add("durationMinutes", minutes);
        }

        if (changes.Points is { } points)
        {
            set.Add("points", points);
        }

        if (changes.DefaultAssignee is { } choice)
        {
            set.Add("defaultAssigneeId", choice.UserId is { } assignee ? ObjectIdConverter.Parse(assignee) : BsonNull.Value);
        }

        if (changes.Active is { } active)
        {
            set.Add("active", active);
        }

        if (changes.Notes is not null)
        {
            set.Add("notes", changes.Notes);
        }

        if (changes.Tags is not null)
        {
            set.Add("tags", new BsonArray(changes.Tags));
        }

        set.Add("updatedAt", new BsonDateTime(updatedAt.UtcDateTime));
        try
        {
            var document = await tasks.FindOneAndUpdateAsync(
                session,
                new BsonDocument("_id", objectId),
                new BsonDocument("$set", set),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
                cancellationToken).ConfigureAwait(false);
            return document is null ? new NotFound() : ToTask(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("update", e);
        }
    }

    public async Task<OneOf<Success, NotFound, PortError>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (!ObjectIdConverter.TryParse(id, out var objectId))
        {
            return new NotFound();
        }

        try
        {
            var result = await tasks.DeleteOneAsync(session, new BsonDocument("_id", objectId), cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.DeletedCount == 1 ? new Success() : new NotFound();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("delete", e);
        }
    }

    public async Task<OneOf<Success, NotFound, PortError>> SetLastCompletedAtAsync(string id, DateTimeOffset? lastCompletedAt, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (!ObjectIdConverter.TryParse(id, out var objectId))
        {
            return new NotFound();
        }

        var set = new BsonDocument
        {
            { "lastCompletedAt", lastCompletedAt is { } at ? new BsonDateTime(at.UtcDateTime) : BsonNull.Value },
            { "updatedAt", new BsonDateTime(updatedAt.UtcDateTime) },
        };
        try
        {
            var result = await tasks.UpdateOneAsync(session, new BsonDocument("_id", objectId), new BsonDocument("$set", set), cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.MatchedCount == 0 ? new NotFound() : new Success();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("set the last completion of", e);
        }
    }

    public async Task<OneOf<int, PortError>> CountInRoomAsync(string roomId, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(roomId, out var id))
        {
            return 0;
        }

        var filter = new BsonDocument("roomId", id);
        try
        {
            var count = MongoTransactionContext.Session is { } session
                ? await tasks.CountDocumentsAsync(session, filter, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await tasks.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (int)Math.Min(count, int.MaxValue);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("count the tasks of a room", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<string>, PortError>> GetIntervalKeysInUseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filter = FilterDefinition<BsonDocument>.Empty;
            var cursor = MongoTransactionContext.Session is { } session
                ? await tasks.DistinctAsync<string>(session, "intervalKey", filter, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await tasks.DistinctAsync<string>("intervalKey", filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            return await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the intervals in use", e);
        }
    }

    private static SortDefinition<BsonDocument> ListOrder => Builders<BsonDocument>.Sort.Ascending("name").Ascending("_id");

    private IFindFluent<BsonDocument, BsonDocument> FindFluent(FilterDefinition<BsonDocument> filter) =>
        MongoTransactionContext.Session is { } session ? tasks.Find(session, filter) : tasks.Find(filter);

    /// <summary>Tasks after the cursor in the order (name, _id).</summary>
    private static BsonDocument AfterFilter(TaskCursor after) => new(
        "$or",
        new BsonArray
        {
            new BsonDocument("name", new BsonDocument("$gt", after.Name)),
            new BsonDocument
            {
                { "name", after.Name },
                { "_id", new BsonDocument("$gt", ObjectIdConverter.Parse(after.Id)) },
            },
        });

    private static HouseholdTask ToTask(BsonDocument document)
    {
        var createdAt = Instant(document, "createdAt") ?? DateTimeOffset.UnixEpoch;
        var duration = document.GetValue("durationMinutes", 1).ToInt32();
        var points = document.TryGetValue("points", out var stored) && stored.IsNumeric
            ? stored.ToInt32()
            : TaskPoints.DefaultForDuration(duration);
        var assignee = document.TryGetValue("defaultAssigneeId", out var assigneeValue) && assigneeValue.IsObjectId
            ? ObjectIdConverter.ToHex(assigneeValue.AsObjectId)
            : null;
        var tags = document.TryGetValue("tags", out var tagValue) && tagValue.IsBsonArray
            ? tagValue.AsBsonArray.Where(t => t.IsString).Select(t => t.AsString).ToList()
            : [];
        return new HouseholdTask(
            ObjectIdConverter.ToHex(document["_id"].AsObjectId),
            document.GetValue("name", string.Empty).AsString,
            ObjectIdConverter.ToHex(document["roomId"].AsObjectId),
            document.GetValue("intervalKey", string.Empty).AsString,
            duration,
            points,
            assignee,
            document.GetValue("active", true).ToBoolean(),
            document.TryGetValue("notes", out var notes) && notes.IsString ? notes.AsString : string.Empty,
            tags,
            Instant(document, "lastCompletedAt"),
            createdAt,
            Instant(document, "updatedAt") ?? createdAt);
    }

    private static DateTimeOffset? Instant(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsValidDateTime
            ? new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
            : null;

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"tasks.failed: could not {operation} ({e.GetType().Name}).");

    private static PortError NoTransaction() =>
        new("tasks.no_transaction: a task can only be written inside a transaction, together with its audit entry.");
}
