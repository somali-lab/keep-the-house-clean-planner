using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Audit;

/// <summary>
/// <see cref="ForReadingAuditLog"/> on <c>auditLog</c>: the documents <see cref="MongoAuditRecorder"/> (and the Node server)
/// write, read into the <see cref="AuditLogEntry"/> read model. Newest first; ties on <c>at</c> are ordered by <c>_id</c>
/// so cursors are stable. The <c>{ entity, entityId, at }</c> and <c>{ at }</c> indexes serve the filters and the order.
/// </summary>
internal sealed class MongoAuditLogReader : ForReadingAuditLog
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> auditLog;

    public MongoAuditLogReader(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        auditLog = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.AuditLog);
    }

    public async Task<OneOf<IReadOnlyList<AuditLogEntry>, PortError>> ListAsync(AuditLogFilter filter, AuditCursor? after, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = ToFilter(filter, after);
        var sort = Builders<BsonDocument>.Sort.Descending("at").Descending("_id");
        try
        {
            var find = MongoTransactionContext.Session is { } session ? auditLog.Find(session, query) : auditLog.Find(query);
            var documents = await find.Sort(sort).Limit(take).ToListAsync(cancellationToken).ConfigureAwait(false);
            return OneOf<IReadOnlyList<AuditLogEntry>, PortError>.FromT0(documents.ConvertAll(ToEntry));
        }
        catch (Exception e) when ((e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel)))
        {
            return new PortError($"audit.read_failed: could not read the audit log ({e.GetType().Name}).");
        }
    }

    private static BsonDocument ToFilter(AuditLogFilter filter, AuditCursor? after)
    {
        var query = new BsonDocument();
        if (filter.Entity is { } entity)
        {
            query.Add("entity", AuditNames.ToWire(entity));
        }

        if (filter.EntityId is not null)
        {
            query.Add("entityId", ObjectIdOrNone(filter.EntityId));
        }

        if (filter.ActorId is not null)
        {
            query.Add("actorId", ObjectIdOrNone(filter.ActorId));
        }

        if (filter.Source is { } source)
        {
            query.Add("source", AuditNames.ToWire(source));
        }

        if (filter.From is not null || filter.To is not null)
        {
            var range = new BsonDocument();
            if (filter.From is { } from)
            {
                range.Add("$gte", new BsonDateTime(from.UtcDateTime));
            }

            if (filter.To is { } to)
            {
                range.Add("$lte", new BsonDateTime(to.UtcDateTime));
            }

            query.Add("at", range);
        }

        if (after is not null)
        {
            var at = new BsonDateTime(after.At.UtcDateTime);
            query.Add("$or", new BsonArray
            {
                new BsonDocument("at", new BsonDocument("$lt", at)),
                new BsonDocument { { "at", at }, { "_id", new BsonDocument("$lt", ObjectIdConverter.Parse(after.Id)) } },
            });
        }

        return query;
    }

    /// <summary>A well-formed id matches the stored ObjectId; the use case validated the format, so anything else matches nothing.</summary>
    private static BsonValue ObjectIdOrNone(string id) =>
        ObjectIdConverter.TryParse(id, out var objectId) ? objectId : BsonNull.Value;

    private static AuditLogEntry ToEntry(BsonDocument document) => new(
        document["_id"].AsObjectId.ToString(),
        document.TryGetValue("at", out var at) && at.IsValidDateTime ? new DateTimeOffset(at.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.UnixEpoch,
        Text(document, "actorId"),
        Text(document, "entity"),
        Text(document, "entityId"),
        Text(document, "action"),
        Object(document, "before") ?? AuditObject.Empty,
        Object(document, "after") ?? AuditObject.Empty,
        Text(document, "source"),
        Object(document, "meta"));

    private static string Text(BsonDocument document, string field) =>
        !document.TryGetValue(field, out var value) ? string.Empty : value.IsObjectId ? value.AsObjectId.ToString() : value.IsString ? value.AsString : string.Empty;

    private static AuditObject? Object(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsBsonDocument ? AuditBsonReader.ToObject(value.AsBsonDocument) : null;
}
