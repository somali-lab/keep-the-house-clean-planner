using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Occurrences;

/// <summary>
/// The reads and the guarded update of the occurrence actions (slice 3.2), on the same <c>occurrences</c> documents. The update writes
/// one <c>$set</c> per field that changed, so a concurrent write to another field of the document is never overwritten, and filters on the
/// state the use case read: of two writers the later one matches nothing and learns that the state changed.
/// </summary>
internal sealed partial class MongoOccurrenceStore
{
    public async Task<OneOf<Occurrence, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(id, out var objectId))
        {
            return new NotFound();
        }

        try
        {
            var filter = new BsonDocument("_id", objectId);
            var find = MongoTransactionContext.Session is { } session ? occurrences.Find(session, filter) : occurrences.Find(filter);
            var document = await find.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null ? new NotFound() : ToOccurrence(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("find an occurrence", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<Occurrence>, PortError>> ListAsync(OccurrenceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = new BsonDocument("date", Between(query.From, query.ToExclusive));
        if (query.AssigneeId is { } assignee)
        {
            if (!ObjectIdConverter.TryParse(assignee, out var person))
            {
                return OneOf<IReadOnlyList<Occurrence>, PortError>.FromT0([]);
            }

            filter.Add("assigneeId", person);
        }

        if (query.Status is { } status)
        {
            filter.Add("status", OccurrenceNames.ToWire(status));
        }

        var descending = query.Order == OccurrenceOrder.Descending;
        var beyond = descending ? "$lt" : "$gt";
        var clauses = new BsonArray { filter };
        if (query.After is { } after)
        {
            var date = new BsonDateTime(after.Date.UtcDateTime);
            var id = ObjectIdConverter.Parse(after.Id);
            clauses.Add(new BsonDocument("$or", new BsonArray
            {
                new BsonDocument("date", new BsonDocument(beyond, date)),
                new BsonDocument { { "date", date }, { "taskNameSnapshot", new BsonDocument(beyond, after.TaskName) } },
                new BsonDocument { { "date", date }, { "taskNameSnapshot", after.TaskName }, { "_id", new BsonDocument(beyond, id) } },
            }));
        }

        try
        {
            var combined = new BsonDocument("$and", clauses);
            var find = MongoTransactionContext.Session is { } session ? occurrences.Find(session, combined) : occurrences.Find(combined);
            var sort = descending
                ? Builders<BsonDocument>.Sort.Descending("date").Descending("taskNameSnapshot").Descending("_id")
                : Builders<BsonDocument>.Sort.Ascending("date").Ascending("taskNameSnapshot").Ascending("_id");
            var documents = await find
                .Sort(sort)
                .Limit(query.Take)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return OneOf<IReadOnlyList<Occurrence>, PortError>.FromT0(documents.ConvertAll(ToOccurrence));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("list the occurrences", e);
        }
    }

    public async Task<OneOf<Occurrence, NotFound, OccurrenceStateChanged, PortError>> UpdateAsync(
        Occurrence before, Occurrence after, OccurrenceGuard guard, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(guard);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (!ObjectIdConverter.TryParse(before.Id, out var id))
        {
            return new NotFound();
        }

        var set = ChangedFields(before, after);
        set.Add("updatedAt", new BsonDateTime(updatedAt.UtcDateTime));
        var filter = new BsonDocument { { "_id", id }, { "status", OccurrenceNames.ToWire(guard.Status) } };
        if (guard.RequireUnassigned)
        {
            filter.Add("assigneeId", BsonNull.Value);
        }

        try
        {
            var result = await occurrences.UpdateOneAsync(session, filter, new BsonDocument("$set", set), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.MatchedCount == 0)
            {
                var exists = await occurrences.CountDocumentsAsync(session, new BsonDocument("_id", id), cancellationToken: cancellationToken).ConfigureAwait(false) > 0;
                return exists ? new OccurrenceStateChanged() : new NotFound();
            }

            return after with { UpdatedAt = updatedAt };
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("update an occurrence", e);
        }
    }

    public async Task<OneOf<LatestCompletion, PortError>> FindLatestCompletionAsync(string taskId, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(taskId, out var task))
        {
            return new LatestCompletion(null);
        }

        try
        {
            var filter = new BsonDocument { { "taskId", task }, { "status", "done" }, { "completedAt", new BsonDocument("$ne", BsonNull.Value) } };
            var find = MongoTransactionContext.Session is { } session ? occurrences.Find(session, filter) : occurrences.Find(filter);
            var latest = await find
                .Sort(Builders<BsonDocument>.Sort.Descending("completedAt"))
                .Limit(1)
                .Project(Builders<BsonDocument>.Projection.Include("completedAt"))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return new LatestCompletion(latest is not null ? Instant(latest, "completedAt") : null);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("find the latest completion", e);
        }
    }

    /// <summary>Exactly the fields of an action that differ: the other fields of the document are never written.</summary>
    private static BsonDocument ChangedFields(Occurrence before, Occurrence after)
    {
        var set = new BsonDocument();
        if (before.Date != after.Date)
        {
            set.Add("date", new BsonDateTime(after.Date.UtcDateTime));
        }

        if (before.CycleId != after.CycleId)
        {
            set.Add("cycleId", ObjectIdConverter.Parse(after.CycleId));
        }

        if (before.AssigneeId != after.AssigneeId)
        {
            set.Add("assigneeId", OptionalId(after.AssigneeId));
        }

        if (before.Status != after.Status)
        {
            set.Add("status", OccurrenceNames.ToWire(after.Status));
        }

        if (before.StatusBeforeCompletion != after.StatusBeforeCompletion)
        {
            set.Add("statusBeforeCompletion", after.StatusBeforeCompletion is { } was ? OccurrenceNames.ToWire(was) : BsonNull.Value);
        }

        if (before.CompletedAt != after.CompletedAt)
        {
            set.Add("completedAt", after.CompletedAt is { } at ? new BsonDateTime(at.UtcDateTime) : BsonNull.Value);
        }

        if (before.CompletedBy != after.CompletedBy)
        {
            set.Add("completedBy", OptionalId(after.CompletedBy));
        }

        if (before.SkipReason != after.SkipReason)
        {
            set.Add("skipReason", after.SkipReason is { } reason ? reason : BsonNull.Value);
        }

        if (before.PointsSnapshot != after.PointsSnapshot)
        {
            set.Add("pointsSnapshot", after.PointsSnapshot is { } points ? points : BsonNull.Value);
        }

        if (after.HasPeriodOwner && (before.PeriodOwnerId != after.PeriodOwnerId || !before.HasPeriodOwner))
        {
            set.Add("periodOwnerId", OptionalId(after.PeriodOwnerId));
        }

        return set;
    }

    private static BsonValue OptionalId(string? id) => id is { } value ? ObjectIdConverter.Parse(value) : BsonNull.Value;
}
