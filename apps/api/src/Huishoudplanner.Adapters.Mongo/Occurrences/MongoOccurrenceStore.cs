using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Occurrences;

/// <summary>
/// <see cref="ForStoringOccurrences"/> on the <c>occurrences</c> collection. The documents are exactly the ones of the Node server
/// (<c>OccurrenceDoc</c>, requirements 3), so both applications read each other's occurrences. Mapped by hand: the driver types stay in
/// this class.
/// </summary>
/// <remarks>
/// <para>Idempotent insert: the Node server inserts with <c>ordered: false</c> and swallows the duplicate-key errors of the partial unique
/// index <c>occurrences_generated_slot_unique</c> (cycle, task, planned day, generated only). Inside a MongoDB transaction any write error
/// aborts the whole transaction, so a duplicate key cannot be swallowed here. Each draft is therefore an upsert on the same key with
/// <c>$setOnInsert</c>: an occurrence that exists is matched and left alone, a missing one is inserted, and the unique index still enforces the
/// rule in the database for concurrent writers (the loser of a race gets a write conflict, which the runner retries). The result tells which
/// drafts were really inserted, which is what gets audited.</para>
/// <para>Writes enlist in the running transaction (<see cref="MongoTransactionContext.Session"/>) and refuse to run without one: an occurrence
/// change without its audit entry must never exist. Reads join the transaction when there is one. A transient transaction error propagates so
/// the runner retries the attempt; any other infrastructure failure is a <see cref="PortError"/> without configuration values.</para>
/// </remarks>
internal sealed partial class MongoOccurrenceStore : ForStoringOccurrences
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> occurrences;

    public MongoOccurrenceStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        occurrences = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Occurrences);
    }

    public async Task<OneOf<IReadOnlyList<Occurrence>, PortError>> InsertGeneratedAsync(IReadOnlyList<NewGeneratedOccurrence> drafts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (drafts.Count == 0)
        {
            return OneOf<IReadOnlyList<Occurrence>, PortError>.FromT0([]);
        }

        var ids = drafts.Select(_ => ObjectId.GenerateNewId()).ToList();
        var models = new List<WriteModel<BsonDocument>>(drafts.Count);
        for (var i = 0; i < drafts.Count; i++)
        {
            models.Add(ToUpsert(drafts[i], ids[i]));
        }

        try
        {
            var result = await occurrences.BulkWriteAsync(session, models, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
            var inserted = result.Upserts.Select(u => u.Index).ToHashSet();
            var stored = new List<Occurrence>(inserted.Count);
            for (var i = 0; i < drafts.Count; i++)
            {
                if (inserted.Contains(i))
                {
                    stored.Add(drafts[i].ToOccurrence(ObjectIdConverter.ToHex(ids[i])));
                }
            }

            return OneOf<IReadOnlyList<Occurrence>, PortError>.FromT0(stored);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("insert the generated occurrences", e);
        }
    }

    public Task<OneOf<IReadOnlyList<Occurrence>, PortError>> FindGeneratedPlannedBetweenAsync(DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken cancellationToken) =>
        QueryAsync(
            new BsonDocument
            {
                { "origin", "generated" },
                { "plannedDate", Between(rangeStart, rangeEnd) },
            },
            "find the generated occurrences",
            cancellationToken);

    public Task<OneOf<IReadOnlyList<Occurrence>, PortError>> FindReplaceableBetweenAsync(DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken cancellationToken) =>
        QueryAsync(
            new BsonDocument
            {
                { "status", "open" },
                { "origin", "generated" },
                { "date", Between(rangeStart, rangeEnd) },
                { "$expr", new BsonDocument("$eq", new BsonArray { "$date", "$plannedDate" }) },
            },
            "find the replaceable occurrences",
            cancellationToken);

    public async Task<OneOf<int, PortError>> DeleteAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        var objectIds = ids.Where(id => ObjectIdConverter.TryParse(id, out _)).Select(ObjectIdConverter.Parse).ToList();
        if (objectIds.Count == 0)
        {
            return 0;
        }

        try
        {
            var result = await occurrences.DeleteManyAsync(session, new BsonDocument("_id", new BsonDocument("$in", new BsonArray(objectIds))), cancellationToken: cancellationToken).ConfigureAwait(false);
            return (int)Math.Min(result.DeletedCount, int.MaxValue);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("delete", e);
        }
    }

    public async Task<OneOf<int, PortError>> UpdateUpcomingRoomSnapshotsAsync(string taskId, DateTimeOffset from, string roomId, string roomName, CancellationToken cancellationToken)
    {
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (!ObjectIdConverter.TryParse(taskId, out var task) || !ObjectIdConverter.TryParse(roomId, out var room))
        {
            return 0;
        }

        try
        {
            var result = await occurrences.UpdateManyAsync(
                session,
                new BsonDocument { { "taskId", task }, { "status", "open" }, { "date", new BsonDocument("$gte", new BsonDateTime(from.UtcDateTime)) } },
                new BsonDocument("$set", new BsonDocument { { "roomIdSnapshot", room }, { "roomNameSnapshot", roomName } }),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return (int)Math.Min(result.ModifiedCount, int.MaxValue);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("update the room snapshots", e);
        }
    }

    private async Task<OneOf<IReadOnlyList<Occurrence>, PortError>> QueryAsync(BsonDocument filter, string operation, CancellationToken cancellationToken)
    {
        try
        {
            var find = MongoTransactionContext.Session is { } session ? occurrences.Find(session, filter) : occurrences.Find(filter);
            var documents = await find
                .Sort(Builders<BsonDocument>.Sort.Ascending("date").Ascending("taskNameSnapshot").Ascending("_id"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return OneOf<IReadOnlyList<Occurrence>, PortError>.FromT0(documents.ConvertAll(ToOccurrence));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed(operation, e);
        }
    }

    private static BsonDocument Between(DateTimeOffset rangeStart, DateTimeOffset rangeEnd) =>
        new() { { "$gte", new BsonDateTime(rangeStart.UtcDateTime) }, { "$lt", new BsonDateTime(rangeEnd.UtcDateTime) } };

    /// <summary>The upsert on the key of the unique slot index; every other field is only set when the occurrence is inserted.</summary>
    private static UpdateOneModel<BsonDocument> ToUpsert(NewGeneratedOccurrence draft, ObjectId id)
    {
        var at = new BsonDateTime(draft.CreatedAt.UtcDateTime);
        var filter = new BsonDocument
        {
            { "cycleId", ObjectIdConverter.Parse(draft.CycleId) },
            { "taskId", ObjectIdConverter.Parse(draft.TaskId) },
            { "plannedDate", new BsonDateTime(draft.Date.UtcDateTime) },
            { "origin", "generated" },
        };
        var onInsert = new BsonDocument
        {
            { "_id", id },
            { "planId", ObjectIdConverter.Parse(draft.PlanId) },
            { "date", new BsonDateTime(draft.Date.UtcDateTime) },
            { "assigneeId", draft.AssigneeId is { } assignee ? ObjectIdConverter.Parse(assignee) : BsonNull.Value },
            { "status", "open" },
            { "statusBeforeCompletion", BsonNull.Value },
            { "completedAt", BsonNull.Value },
            { "completedBy", BsonNull.Value },
            { "skipReason", BsonNull.Value },
            { "durationMinutesSnapshot", draft.DurationMinutesSnapshot },
            { "taskNameSnapshot", draft.TaskNameSnapshot },
            { "roomIdSnapshot", ObjectIdConverter.Parse(draft.RoomIdSnapshot) },
            { "roomNameSnapshot", draft.RoomNameSnapshot is { } room ? room : BsonNull.Value },
            { "createdAt", at },
            { "updatedAt", at },
        };
        return new UpdateOneModel<BsonDocument>(filter, new BsonDocument("$setOnInsert", onInsert)) { IsUpsert = true };
    }

    internal static Occurrence ToOccurrence(BsonDocument document)
    {
        var createdAt = Instant(document, "createdAt") ?? DateTimeOffset.UnixEpoch;
        var date = Instant(document, "date") ?? DateTimeOffset.UnixEpoch;
        return new Occurrence(
            ObjectIdConverter.ToHex(document["_id"].AsObjectId),
            Id(document, "taskId"),
            Id(document, "cycleId") ?? string.Empty,
            Id(document, "planId"),
            date,
            Instant(document, "plannedDate") ?? date,
            Id(document, "assigneeId"),
            Status(document, "status") ?? OccurrenceStatus.Open,
            Status(document, "statusBeforeCompletion"),
            Instant(document, "completedAt"),
            Id(document, "completedBy"),
            Text(document, "skipReason"),
            document.TryGetValue("durationMinutesSnapshot", out var duration) && duration.IsNumeric ? duration.ToInt32() : 1,
            Text(document, "taskNameSnapshot") ?? string.Empty,
            Id(document, "roomIdSnapshot"),
            Text(document, "roomNameSnapshot"),
            document.TryGetValue("origin", out var origin) && origin.IsString && OccurrenceNames.TryParseOrigin(origin.AsString, out var parsedOrigin)
                ? parsedOrigin
                : OccurrenceOrigin.Generated,
            createdAt,
            Instant(document, "updatedAt") ?? createdAt,
            document.TryGetValue("recordedDone", out var recorded) && recorded.IsBoolean && recorded.AsBoolean,
            Text(document, "requestId"),
            Number(document, "pointsSnapshot"),
            Number(document, "pointsOverride"),
            Id(document, "periodOwnerId"),
            document.Contains("periodOwnerId"));
    }

    private static string? Id(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsObjectId ? ObjectIdConverter.ToHex(value.AsObjectId) : null;

    private static string? Text(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsString ? value.AsString : null;

    private static int? Number(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsNumeric ? value.ToInt32() : null;

    private static OccurrenceStatus? Status(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsString && OccurrenceNames.TryParseStatus(value.AsString, out var status) ? status : null;

    private static DateTimeOffset? Instant(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsValidDateTime
            ? new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
            : null;

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"occurrences.failed: could not {operation} ({e.GetType().Name}).");

    private static PortError NoTransaction() =>
        new("occurrences.no_transaction: an occurrence can only be written inside a transaction, together with its audit entry.");
}
