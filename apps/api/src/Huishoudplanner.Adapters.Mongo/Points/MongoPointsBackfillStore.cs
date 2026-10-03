using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Limits;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;
using BonusStatus = Huishoudplanner.Domain.Bonuses.OccurrenceStatus;

namespace Huishoudplanner.Adapters.Mongo.Points;

/// <summary>
/// <see cref="ForBackfillingPoints"/> on the <c>occurrences</c> and <c>tasks</c> collections of the Node server: it reads the done occurrences and
/// the task values the reconciliation needs, and writes the two fields an installation from before points gains, <c>tasks.points</c> and
/// <c>occurrences.pointsSnapshot</c>. Both writes filter on the missing field (a value that appeared meanwhile is never overwritten), so a second
/// run matches nothing. Mapped by hand: the driver types stay in this class.
/// </summary>
/// <remarks>
/// The writes enlist in the running transaction and refuse to run without one; reads join the transaction when there is one. They are not audited
/// one by one: the reconciliation records one summary entry.
/// </remarks>
internal sealed class MongoPointsBackfillStore : ForBackfillingPoints
{
    private const string TransientLabel = "TransientTransactionError";

    private static readonly BsonDocument MissingPoints = new("$or", new BsonArray
    {
        new BsonDocument("points", new BsonDocument("$exists", false)),
        new BsonDocument("points", BsonNull.Value),
    });

    private readonly IMongoCollection<BsonDocument> occurrences;
    private readonly IMongoCollection<BsonDocument> tasks;

    public MongoPointsBackfillStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        var database = MongoClientFactory.GetDatabase(client, options);
        occurrences = database.GetCollection<BsonDocument>(MongoCollections.Occurrences);
        tasks = database.GetCollection<BsonDocument>(MongoCollections.Tasks);
    }

    public async Task<OneOf<IReadOnlyList<ExecutionSource>, PortError>> FindDoneOccurrencesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filter = new BsonDocument("status", "done");
            var find = MongoTransactionContext.Session is { } session ? occurrences.Find(session, filter) : occurrences.Find(filter);
            var documents = await find
                .Project(new BsonDocument
                {
                    { "taskId", 1 }, { "date", 1 }, { "completedBy", 1 }, { "assigneeId", 1 }, { "pointsSnapshot", 1 },
                    { "pointsOverride", 1 }, { "durationMinutesSnapshot", 1 }, { "taskNameSnapshot", 1 },
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return OneOf<IReadOnlyList<ExecutionSource>, PortError>.FromT0([.. documents.Select(ToSource)]);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the done occurrences", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<BonusSource>, PortError>> FindBonusOccurrencesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filter = new BsonDocument();
            var find = MongoTransactionContext.Session is { } session ? occurrences.Find(session, filter) : occurrences.Find(filter);
            var documents = await find
                .Project(new BsonDocument
                {
                    { "status", 1 }, { "plannedDate", 1 }, { "date", 1 }, { "recordedDone", 1 }, { "assigneeId", 1 },
                    { "periodOwnerId", 1 }, { "completedBy", 1 }, { "completedAt", 1 },
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return OneOf<IReadOnlyList<BonusSource>, PortError>.FromT0([.. documents.Select(ToBonusSource)]);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the occurrences for the bonuses", e);
        }
    }

    private static BonusSource ToBonusSource(BsonDocument document)
    {
        var status = document.TryGetValue("status", out var s) && s.IsString
            ? s.AsString switch
            {
                "open" => BonusStatus.Open,
                "done" => BonusStatus.Done,
                "skipped" => BonusStatus.Skipped,
                _ => (BonusStatus?)null,
            }
            : null;
        // A completion instant that is present but is no date makes the row unreadable (Node throws on it), like an unknown status.
        var hasCompletion = document.TryGetValue("completedAt", out var completion) && !completion.IsBsonNull;
        var completedAt = hasCompletion ? Instant(completion!) : null;
        var hasFrozen = document.TryGetValue("periodOwnerId", out var frozen);
        return new BonusSource(
            ObjectIdConverter.ToHex(document["_id"].AsObjectId),
            hasCompletion && completedAt is null ? null : status,
            document.TryGetValue("plannedDate", out var planned) ? Instant(planned) : null,
            document.TryGetValue("date", out var date) ? Instant(date) : null,
            document.TryGetValue("recordedDone", out var recorded) && recorded.IsBoolean && recorded.AsBoolean,
            Id(document, "assigneeId"),
            hasFrozen,
            hasFrozen && frozen!.IsObjectId ? ObjectIdConverter.ToHex(frozen.AsObjectId) : null,
            Id(document, "completedBy"),
            completedAt);
    }

    private static DateTimeOffset? Instant(BsonValue value) =>
        value.IsValidDateTime ? new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero) : null;

    public async Task<OneOf<IReadOnlyList<TaskPointValue>, PortError>> FindTaskPointValuesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var find = MongoTransactionContext.Session is { } session ? tasks.Find(session, new BsonDocument()) : tasks.Find(new BsonDocument());
            var documents = await find
                .Project(new BsonDocument { { "points", 1 }, { "durationMinutes", 1 } })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return OneOf<IReadOnlyList<TaskPointValue>, PortError>.FromT0([.. documents.Select(d => new TaskPointValue(
                ObjectIdConverter.ToHex(d["_id"].AsObjectId),
                Number(d, "points"),
                Number(d, "durationMinutes") ?? 1))]);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the task points", e);
        }
    }

    public async Task<OneOf<DefaultedTasks, PortError>> DefaultMissingTaskPointsAsync(CancellationToken cancellationToken)
    {
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        try
        {
            var missing = await tasks.Find(session, MissingPoints).Project(new BsonDocument("durationMinutes", 1)).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (missing.Count == 0)
            {
                return new DefaultedTasks([], 0);
            }

            var models = missing.Select(task => (WriteModel<BsonDocument>)new UpdateOneModel<BsonDocument>(
                new BsonDocument { { "_id", task["_id"] }, { "$or", MissingPoints["$or"] } },
                new BsonDocument("$set", new BsonDocument("points", TaskPoints.DefaultForDuration(task.TryGetValue("durationMinutes", out var minutes) && minutes.IsNumeric ? minutes.ToDouble() : 1))))).ToList();
            var result = await tasks.BulkWriteAsync(session, models, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new DefaultedTasks([.. missing.Select(t => ObjectIdConverter.ToHex(t["_id"].AsObjectId))], (int)Math.Min(result.ModifiedCount, int.MaxValue));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("default the task points", e);
        }
    }

    public async Task<OneOf<int, PortError>> SetMissingSnapshotsAsync(IReadOnlyList<SnapshotWrite> snapshots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (snapshots.Count == 0)
        {
            return 0;
        }

        try
        {
            // The filter keeps a snapshot that was written in the meantime; null matches a missing field as well.
            var models = snapshots
                .Where(s => ObjectIdConverter.TryParse(s.OccurrenceId, out _))
                .Select(s => (WriteModel<BsonDocument>)new UpdateOneModel<BsonDocument>(
                    new BsonDocument { { "_id", ObjectIdConverter.Parse(s.OccurrenceId) }, { "status", "done" }, { "pointsSnapshot", BsonNull.Value } },
                    new BsonDocument("$set", new BsonDocument("pointsSnapshot", s.Points))))
                .ToList();
            if (models.Count == 0)
            {
                return 0;
            }

            var result = await occurrences.BulkWriteAsync(session, models, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (int)Math.Min(result.ModifiedCount, int.MaxValue);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("set the points snapshots", e);
        }
    }

    private static ExecutionSource ToSource(BsonDocument document) => new(
        ObjectIdConverter.ToHex(document["_id"].AsObjectId),
        Id(document, "taskId"),
        document.TryGetValue("date", out var date) && date.IsValidDateTime ? new DateTimeOffset(date.ToUniversalTime(), TimeSpan.Zero) : null,
        OccurrenceStatus.Done,
        Id(document, "completedBy"),
        Id(document, "assigneeId"),
        Number(document, "pointsSnapshot"),
        Number(document, "pointsOverride"),
        Number(document, "durationMinutesSnapshot") ?? 1,
        document.TryGetValue("taskNameSnapshot", out var name) && name.IsString ? name.AsString : string.Empty);

    private static string? Id(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsObjectId ? ObjectIdConverter.ToHex(value.AsObjectId) : null;

    private static int? Number(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsNumeric ? value.ToInt32() : null;

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"points.failed: could not {operation} ({e.GetType().Name}).");

    private static PortError NoTransaction() =>
        new("points.no_transaction: the points fields can only be migrated inside a transaction, together with the summary of the reconciliation.");
}
