using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Rooms;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Rooms;

/// <summary>
/// <see cref="ForStoringRooms"/> on the <c>rooms</c> collection. The documents are exactly the ones of the Node server
/// (<c>RoomDoc</c>: <c>_id, name, sortOrder, active, virtual, createdAt, updatedAt</c>), so both applications read each
/// other's rooms. Mapped by hand: the driver types stay in this class.
/// </summary>
/// <remarks>
/// Writes enlist in the running transaction (<see cref="MongoTransactionContext.Session"/>) and refuse to run without one: a room
/// change without its audit entry must never exist. Reads join the transaction when there is one. A transient transaction
/// error propagates so the runner retries the attempt; any other infrastructure failure is a <see cref="PortError"/>
/// without configuration values.
/// </remarks>
internal sealed class MongoRoomStore : ForStoringRooms
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> rooms;

    public MongoRoomStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        rooms = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Rooms);
    }

    public async Task<OneOf<IReadOnlyList<Room>, PortError>> ListAsync(bool? active, RoomCursor? after, int take, CancellationToken cancellationToken)
    {
        var filters = new List<FilterDefinition<BsonDocument>>();
        if (active is { } flag)
        {
            filters.Add(new BsonDocument("active", flag));
        }

        if (after is not null)
        {
            filters.Add(AfterFilter(after));
        }

        var filter = filters.Count == 0 ? FilterDefinition<BsonDocument>.Empty : Builders<BsonDocument>.Filter.And(filters);
        var sort = Builders<BsonDocument>.Sort.Ascending("sortOrder").Ascending("name").Ascending("_id");
        try
        {
            var documents = await FindFluent(filter).Sort(sort).Limit(take).ToListAsync(cancellationToken).ConfigureAwait(false);
            return OneOf<IReadOnlyList<Room>, PortError>.FromT0(documents.ConvertAll(ToRoom));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("list", e);
        }
    }

    public async Task<OneOf<Room, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(id, out var objectId))
        {
            return new NotFound();
        }

        try
        {
            var document = await FindFluent(new BsonDocument("_id", objectId)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null ? new NotFound() : ToRoom(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("find", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<Room>, PortError>> FindManyAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var objectIds = ids.Where(id => ObjectIdConverter.TryParse(id, out _)).Select(ObjectIdConverter.Parse).ToList();
        if (objectIds.Count == 0)
        {
            return OneOf<IReadOnlyList<Room>, PortError>.FromT0([]);
        }

        try
        {
            var documents = await FindFluent(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(objectIds))))
                .Sort(Builders<BsonDocument>.Sort.Ascending("sortOrder").Ascending("name").Ascending("_id"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return OneOf<IReadOnlyList<Room>, PortError>.FromT0(documents.ConvertAll(ToRoom));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("find several", e);
        }
    }

    public async Task<OneOf<Room, NotFound, PortError>> FindLastAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await FindFluent(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Descending("sortOrder"))
                .Limit(1)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return document is null ? new NotFound() : ToRoom(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("find the last room", e);
        }
    }

    public async Task<OneOf<Room, PortError>> InsertAsync(NewRoom room, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        var now = new BsonDateTime(room.CreatedAt.UtcDateTime);
        var document = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "name", room.Name },
            { "sortOrder", room.SortOrder },
            { "active", room.Active },
            { "virtual", room.Virtual },
            { "createdAt", now },
            { "updatedAt", now },
        };
        try
        {
            await rooms.InsertOneAsync(session, document, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ToRoom(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("insert", e);
        }
    }

    public async Task<OneOf<Room, NotFound, PortError>> UpdateAsync(string id, RoomChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken)
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

        if (changes.SortOrder is { } sortOrder)
        {
            set.Add("sortOrder", sortOrder);
        }

        if (changes.Active is { } active)
        {
            set.Add("active", active);
        }

        if (changes.Virtual is { } isVirtual)
        {
            set.Add("virtual", isVirtual);
        }

        set.Add("updatedAt", new BsonDateTime(updatedAt.UtcDateTime));
        try
        {
            var document = await rooms.FindOneAndUpdateAsync(
                session,
                new BsonDocument("_id", objectId),
                new BsonDocument("$set", set),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
                cancellationToken).ConfigureAwait(false);
            return document is null ? new NotFound() : ToRoom(document);
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
            var result = await rooms.DeleteOneAsync(session, new BsonDocument("_id", objectId), cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.DeletedCount == 1 ? new Success() : new NotFound();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("delete", e);
        }
    }

    private IFindFluent<BsonDocument, BsonDocument> FindFluent(FilterDefinition<BsonDocument> filter) =>
        MongoTransactionContext.Session is { } session ? rooms.Find(session, filter) : rooms.Find(filter);

    /// <summary>Rooms after the cursor in the order (sortOrder, name, _id).</summary>
    private static BsonDocument AfterFilter(RoomCursor after) => new(
        "$or",
        new BsonArray
        {
            new BsonDocument("sortOrder", new BsonDocument("$gt", after.SortOrder)),
            new BsonDocument { { "sortOrder", after.SortOrder }, { "name", new BsonDocument("$gt", after.Name) } },
            new BsonDocument
            {
                { "sortOrder", after.SortOrder },
                { "name", after.Name },
                { "_id", new BsonDocument("$gt", ObjectIdConverter.Parse(after.Id)) },
            },
        });

    private static Room ToRoom(BsonDocument document)
    {
        var createdAt = Instant(document, "createdAt");
        return new Room(
            ObjectIdConverter.ToHex(document["_id"].AsObjectId),
            document.GetValue("name", string.Empty).AsString,
            document.GetValue("sortOrder", 0).ToInt32(),
            document.GetValue("active", true).ToBoolean(),
            document.GetValue("virtual", false).ToBoolean(),
            createdAt,
            document.Contains("updatedAt") ? Instant(document, "updatedAt") : createdAt);
    }

    private static DateTimeOffset Instant(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsValidDateTime
            ? new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
            : DateTimeOffset.UnixEpoch;

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"rooms.failed: could not {operation} ({e.GetType().Name}).");

    private static PortError NoTransaction() =>
        new("rooms.no_transaction: a room can only be written inside a transaction, together with its audit entry.");
}
