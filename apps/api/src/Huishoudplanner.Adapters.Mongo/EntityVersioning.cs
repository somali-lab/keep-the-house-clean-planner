using Huishoudplanner.Domain.Concurrency;
using Huishoudplanner.Domain.Errors;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// The optimistic concurrency version of a document (ADR-0022), kept in one place for every store: the field, how it reads, how a new
/// document starts, the filter of a conditional write, the <c>$inc</c> that raises it in the same write, and how a conditional write that
/// matched nothing is told apart (the entity is gone, or its version moved on). The Node server ignores the extra field; a document without
/// it reads as version 0 and its first write makes it 1.
/// </summary>
internal static class EntityVersioning
{
    public const string Field = "version";

    /// <summary>The stored version; a document without one (or a non-number) reads as <see cref="EntityVersion.Legacy"/>.</summary>
    public static int VersionOf(BsonDocument document) =>
        document.TryGetValue(Field, out var value) && value.IsNumeric ? value.ToInt32() : EntityVersion.Legacy;

    /// <summary>The filter of a write on one document: its id and, when the caller expects a version, that version (0 also matches a missing field).</summary>
    public static BsonDocument Filter(ObjectId id, int? expectedVersion)
    {
        var filter = new BsonDocument("_id", id);
        if (expectedVersion is { } version)
        {
            filter.Add(Field, version == EntityVersion.Legacy ? new BsonDocument("$in", new BsonArray { 0, BsonNull.Value }) : new BsonDocument("$eq", version));
        }

        return filter;
    }

    /// <summary>The same update with the version raised by one (a missing field becomes 1).</summary>
    public static BsonDocument Raise(BsonDocument update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.TryGetValue("$inc", out var existing) && existing.IsBsonDocument)
        {
            existing.AsBsonDocument.Set(Field, 1);
        }
        else
        {
            update["$inc"] = new BsonDocument(Field, 1);
        }

        return update;
    }

    /// <summary>
    /// A conditional write that matched nothing: <see cref="PreconditionFailed"/> (with the stored version) when the document exists, otherwise
    /// <see cref="NotFound"/>. Read in the same session as the write, so the answer is as consistent as the write was.
    /// </summary>
    public static async Task<OneOf<NotFound, PreconditionFailed>> ExplainMissAsync(
        IMongoCollection<BsonDocument> collection, IClientSessionHandle session, ObjectId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(session);
        var stored = await collection.Find(session, new BsonDocument("_id", id))
            .Project(new BsonDocument(Field, 1))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return stored is null ? new NotFound() : new PreconditionFailed(VersionOf(stored));
    }
}
