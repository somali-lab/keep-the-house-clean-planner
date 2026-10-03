using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// <see cref="ForRecordingAudit"/> on the <c>auditLog</c> collection (ADR-0004, ADR-0021). The document has exactly the
/// fields of <c>AuditEntryDoc</c> in the Node server, in the same order and BSON types, so both applications read each
/// other's entries during the parallel run: <c>_id, at, actorId, entity, entityId, action, before, after, source</c>
/// and <c>meta</c> only when present. The role of the actor is not stored.
/// </summary>
/// <remarks>
/// <para>The insert always enlists in the session of the running <see cref="MongoTransactionRunner"/> transaction
/// (<see cref="MongoTransactionContext.Session"/>). Called outside a transaction it writes nothing and returns
/// <c>audit.no_transaction</c>: an audit entry without the entity write it describes (or the reverse) must never
/// exist, and a plain single insert would be exactly that. It is a value, not an exception, because the port contract
/// is errors as values; the use case that gets it aborts the transaction it should have been in.</para>
/// <para>A transient transaction error (<c>TransientTransactionError</c>) is not turned into a value: it propagates so
/// the runner retries the whole attempt. Any other <see cref="MongoException"/> or <see cref="TimeoutException"/> is
/// a value-free <see cref="PortError"/>; the use case then aborts, so no entity write survives either.</para>
/// <para>The moment is <see cref="TimeProvider.GetUtcNow"/> (the BSON date keeps milliseconds) and the id is an
/// <see cref="ObjectId"/> generated for that moment, so ids order like the entries do.</para>
/// </remarks>
internal sealed class MongoAuditRecorder : ForRecordingAudit
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> auditLog;
    private readonly TimeProvider timeProvider;

    public MongoAuditRecorder(IMongoClient client, MongoOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        auditLog = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.AuditLog);
        this.timeProvider = timeProvider;
    }

    public async Task<OneOf<Success, PortError>> RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var session = MongoTransactionContext.Session;
        if (session is not { IsInTransaction: true })
        {
            return new PortError("audit.no_transaction: an audit entry can only be recorded inside a transaction, together with its entity write.");
        }

        if (!ObjectIdConverter.TryParse(entry.Actor.ActorId, out var actorId) ||
            !ObjectIdConverter.TryParse(entry.EntityId, out var entityId))
        {
            return new PortError("audit.invalid_entry: the actor id and the entity id must be 24 character hexadecimal ids.");
        }

        var now = timeProvider.GetUtcNow();
        var document = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId(now.UtcDateTime) },
            { "at", new BsonDateTime(now.UtcDateTime) },
            { "actorId", actorId },
            { "entity", AuditNames.ToWire(entry.Entity) },
            { "entityId", entityId },
            { "action", AuditNames.ToWire(entry.Action) },
            { "before", AuditBson.ToDocument(entry.Before) },
            { "after", AuditBson.ToDocument(entry.After) },
            { "source", AuditNames.ToWire(entry.Actor.Source) },
        };
        if (entry.Meta is not null)
        {
            document.Add("meta", AuditBson.ToDocument(entry.Meta));
        }

        try
        {
            await auditLog.InsertOneAsync(session, document, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new Success();
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return new PortError($"audit.failed: the audit entry could not be written ({e.GetType().Name}).");
        }
    }

    private static bool IsInfrastructureFailure(Exception e) => e is MongoException or TimeoutException;

    private static bool IsTransient(Exception e) => e is MongoException m && m.HasErrorLabel(TransientLabel);
}
