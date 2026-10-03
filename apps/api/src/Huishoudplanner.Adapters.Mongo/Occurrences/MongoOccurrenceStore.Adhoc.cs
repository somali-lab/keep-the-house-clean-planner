using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Occurrences;

/// <summary>
/// The ad-hoc occurrences (slice 3.3): extra executions and one-off tasks (ADR-0009), on the same <c>occurrences</c> documents. The insert relies on the
/// partial unique index <c>occurrences_request_id_unique</c> for idempotency: a duplicate key is reported as <see cref="RequestKeyTaken"/> instead
/// of being swallowed. Inside a transaction a write error makes the server abandon the transaction, so the caller must roll back and start again.
/// A write conflict with a concurrent insert of the same key is a transient transaction error and propagates to the transaction runner, which retries.
/// </summary>
internal sealed partial class MongoOccurrenceStore
{
    public async Task<OneOf<Occurrence, NotFound, PortError>> FindByRequestIdAsync(string requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        try
        {
            var filter = new BsonDocument("requestId", requestId);
            var find = MongoTransactionContext.Session is { } session ? occurrences.Find(session, filter) : occurrences.Find(filter);
            var document = await find.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null ? new NotFound() : ToOccurrence(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("find an occurrence by request key", e);
        }
    }

    public async Task<OneOf<int, PortError>> CountOpenOfTaskOnAsync(string taskId, DateTimeOffset day, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(taskId, out var task))
        {
            return 0;
        }

        try
        {
            var filter = new BsonDocument { { "taskId", task }, { "status", "open" }, { "date", new BsonDateTime(day.UtcDateTime) } };
            var count = MongoTransactionContext.Session is { } session
                ? await occurrences.CountDocumentsAsync(session, filter, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await occurrences.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (int)Math.Min(count, int.MaxValue);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("count the open occurrences of a task", e);
        }
    }

    public async Task<OneOf<Occurrence, RequestKeyTaken, PortError>> InsertAdhocAsync(NewAdhocOccurrence draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        var id = ObjectId.GenerateNewId();
        try
        {
            await occurrences.InsertOneAsync(session, ToDocument(draft, id), cancellationToken: cancellationToken).ConfigureAwait(false);
            return draft.ToOccurrence(ObjectIdConverter.ToHex(id));
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new RequestKeyTaken();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("insert an ad-hoc occurrence", e);
        }
    }

    public async Task<OneOf<Occurrence, NotFound, PortError>> DeleteRecordedAsync(string id, CancellationToken cancellationToken)
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
            var filter = new BsonDocument { { "_id", objectId }, { "origin", "adhoc" }, { "recordedDone", true }, { "status", "done" } };
            var deleted = await occurrences.FindOneAndDeleteAsync(session, filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            return deleted is null ? new NotFound() : ToOccurrence(deleted);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("delete recorded work", e);
        }
    }

    /// <summary>The document the Node server inserts for an ad-hoc occurrence: every field present, <c>requestId</c> null without a key, the points fields only when set.</summary>
    private static BsonDocument ToDocument(NewAdhocOccurrence draft, ObjectId id)
    {
        var date = new BsonDateTime(draft.Date.UtcDateTime);
        var at = new BsonDateTime(draft.CreatedAt.UtcDateTime);
        var document = new BsonDocument
        {
            { "_id", id },
            { "taskId", OptionalId(draft.TaskId) },
            { "cycleId", ObjectIdConverter.Parse(draft.CycleId) },
            { "planId", BsonNull.Value },
            { "date", date },
            { "plannedDate", date },
            { "assigneeId", OptionalId(draft.AssigneeId) },
            { "status", draft.Done ? "done" : "open" },
            { "statusBeforeCompletion", BsonNull.Value },
            { "completedAt", draft.CompletedAt is { } completedAt ? new BsonDateTime(completedAt.UtcDateTime) : BsonNull.Value },
            { "completedBy", draft.Done ? OptionalId(draft.AssigneeId) : BsonNull.Value },
            { "skipReason", BsonNull.Value },
            { "durationMinutesSnapshot", draft.DurationMinutesSnapshot },
            { "taskNameSnapshot", draft.TaskNameSnapshot },
            { "roomIdSnapshot", OptionalId(draft.RoomIdSnapshot) },
            { "roomNameSnapshot", draft.RoomNameSnapshot is { } room ? room : BsonNull.Value },
            { "origin", "adhoc" },
            { "recordedDone", draft.Done },
            { "requestId", draft.RequestId is { } key ? key : BsonNull.Value },
        };
        if (draft.PointsOverride is { } chosen)
        {
            document.Add("pointsOverride", chosen);
        }

        if (draft.PointsSnapshot is { } snapshot)
        {
            document.Add("pointsSnapshot", snapshot);
        }

        document.Add("createdAt", at);
        document.Add("updatedAt", at);
        return document;
    }
}
