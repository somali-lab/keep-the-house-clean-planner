using Huishoudplanner.Domain.Bonuses;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Points;

/// <summary>
/// <see cref="ForStoringPointEntries"/> on the <c>pointEntries</c> collection. The documents are exactly the ones of the Node server
/// (<c>PointEntryDoc</c>, requirements 3), so both applications read each other's entries; every kind is read, only executions are written here.
/// Mapped by hand: the driver types stay in this class. The unique indexes (<c>key</c>, and <c>requestId</c> for redemptions) are in the
/// <see cref="IndexCatalog"/>.
/// </summary>
/// <remarks>
/// Writes enlist in the running transaction (<see cref="MongoTransactionContext.Session"/>) and refuse to run without one: a ledger change without
/// its audit entry must never exist. Reads join the transaction when there is one. A transient transaction error propagates so the runner retries
/// the attempt; any other infrastructure failure is a <see cref="PortError"/> without configuration values. Inside a transaction a write error
/// aborts the whole transaction, so a duplicate key is not swallowed: the unique index makes the loser of a race fail its attempt, and the retry
/// reads the winner's entry.
/// </remarks>
internal sealed class MongoPointEntryStore : ForStoringPointEntries
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> entries;

    public MongoPointEntryStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        entries = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.PointEntries);
    }

    public async Task<OneOf<PointEntry, NotFound, PortError>> FindByKeyAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var filter = new BsonDocument("key", key);
            var find = MongoTransactionContext.Session is { } session ? entries.Find(session, filter) : entries.Find(filter);
            var document = await find.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is not null && ToEntry(document) is { } entry ? entry : new NotFound();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the entry", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<PointEntry>, PortError>> FindExecutionEntriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filter = new BsonDocument("kind", "execution");
            var find = MongoTransactionContext.Session is { } session ? entries.Find(session, filter) : entries.Find(filter);
            var documents = await find.Sort(Sort).ToListAsync(cancellationToken).ConfigureAwait(false);
            return OneOf<IReadOnlyList<PointEntry>, PortError>.FromT0([.. documents.Select(ToEntry).OfType<PointEntry>()]);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the execution entries", e);
        }
    }

    public async Task<OneOf<UnreadableEntries, PortError>> FindUnreadableExecutionEntriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filter = new BsonDocument
            {
                { "kind", "execution" },
                { "$or", new BsonArray
                    {
                        new BsonDocument("key", new BsonDocument("$not", new BsonDocument("$type", "string"))),
                        new BsonDocument("personId", new BsonDocument("$not", new BsonDocument("$type", "objectId"))),
                    }
                },
            };
            var find = MongoTransactionContext.Session is { } session ? entries.Find(session, filter) : entries.Find(filter);
            var documents = await find.Project(new BsonDocument("key", 1)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var keys = documents.Where(d => d.TryGetValue("key", out var k) && k.IsString).Select(d => d["key"].AsString).ToList();
            return new UnreadableEntries(keys, documents.Count - keys.Count);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the unreadable execution entries", e);
        }
    }

    public async Task<OneOf<PointEntry, PortError>> InsertExecutionAsync(
        string key, ExecutionEntryFields fields, PointEntrySource source, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        var document = ToDocument(ObjectId.GenerateNewId(), key, fields, source, at);
        try
        {
            await entries.InsertOneAsync(session, document, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ToEntry(document)!;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A concurrent transaction wrote the same key: not a failure but a lost race, so the runner reruns the attempt, which then reads the winner.
            e.AddErrorLabel(TransientLabel);
            throw;
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("insert the entry", e);
        }
    }

    public async Task<OneOf<PointEntry, NotFound, PortError>> UpdateExecutionAsync(
        PointEntry current, ExecutionEntryFields fields, PointEntrySource source, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fields);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (!ObjectIdConverter.TryParse(current.Id, out var id))
        {
            return new NotFound();
        }

        try
        {
            var after = await entries.FindOneAndUpdateAsync(
                session,
                new BsonDocument("_id", id),
                new BsonDocument("$set", SetFields(fields, source, at)),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
                cancellationToken).ConfigureAwait(false);
            return after is not null && ToEntry(after) is { } entry ? entry : new NotFound();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("update the entry", e);
        }
    }

    public async Task<OneOf<bool, PortError>> DeleteAsync(PointEntry current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (!ObjectIdConverter.TryParse(current.Id, out var id))
        {
            return false;
        }

        try
        {
            var deleted = await entries.DeleteOneAsync(session, new BsonDocument("_id", id), cancellationToken: cancellationToken).ConfigureAwait(false);
            return deleted.DeletedCount > 0;
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("delete the entry", e);
        }
    }

    public async Task<OneOf<AppliedPointEntryChanges, PortError>> ApplyChangesAsync(PointEntryChanges changes, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        var models = new List<WriteModel<BsonDocument>>();
        foreach (var insert in changes.Inserts)
        {
            models.Add(new InsertOneModel<BsonDocument>(ToDocument(ObjectId.GenerateNewId(), insert.Key, insert.Fields, PointEntrySource.Backfill, at)));
        }

        foreach (var update in changes.Updates)
        {
            if (SameAsRead(update.Current) is { } filter)
            {
                models.Add(new UpdateOneModel<BsonDocument>(filter, new BsonDocument("$set", SetFields(update.Fields, PointEntrySource.Recompute, at))));
            }
        }

        foreach (var delete in changes.Deletes)
        {
            if (SameAsRead(delete) is { } filter)
            {
                models.Add(new DeleteOneModel<BsonDocument>(filter));
            }
        }

        if (models.Count == 0)
        {
            return new AppliedPointEntryChanges(0, 0, 0);
        }

        try
        {
            var result = await entries.BulkWriteAsync(session, models, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
            return new AppliedPointEntryChanges(Count(result.InsertedCount), Count(result.ModifiedCount), Count(result.DeletedCount));
        }
        catch (MongoBulkWriteException e) when (e.WriteErrors.Any(w => w.Category == ServerErrorCategory.DuplicateKey))
        {
            // A live sync inserted a key of this run meanwhile: the attempt reruns and reads it.
            e.AddErrorLabel(TransientLabel);
            throw;
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("apply the reconciliation", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<PointEntry>, PortError>> FindBonusEntriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filter = new BsonDocument("kind", new BsonDocument("$in", new BsonArray(BonusKinds.All.Select(k => k.ToLedgerKind()))));
            var find = MongoTransactionContext.Session is { } session ? entries.Find(session, filter) : entries.Find(filter);
            var documents = await find.Sort(Sort).ToListAsync(cancellationToken).ConfigureAwait(false);
            return OneOf<IReadOnlyList<PointEntry>, PortError>.FromT0([.. documents.Select(ToEntry).OfType<PointEntry>()]);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the bonus entries", e);
        }
    }

    public async Task<OneOf<AppliedBonusChanges, PortError>> ApplyBonusChangesAsync(BonusEntryChanges changes, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        // The deletes first (compare-and-set on the entry as it was read), then the inserts, in one ordered bulk write.
        var models = new List<WriteModel<BsonDocument>>();
        var deleted = new List<(string Key, ObjectId Id)>();
        foreach (var delete in changes.Deletes)
        {
            if (SameAsRead(delete) is { } filter && ObjectIdConverter.TryParse(delete.Id, out var id))
            {
                filter.Add("key", delete.Key);
                models.Add(new DeleteOneModel<BsonDocument>(filter));
                deleted.Add((delete.Key, id));
            }
        }

        var inserted = new List<(string Key, ObjectId Id)>();
        foreach (var insert in changes.Inserts)
        {
            var id = ObjectId.GenerateNewId();
            models.Add(new InsertOneModel<BsonDocument>(ToDocument(id, insert, at)));
            inserted.Add((insert.Key, id));
        }

        if (models.Count == 0)
        {
            return AppliedBonusChanges.None;
        }

        try
        {
            await entries.BulkWriteAsync(session, models, new BulkWriteOptions { IsOrdered = true }, cancellationToken).ConfigureAwait(false);
            // Read back what is there, so only the writes that happened are reported.
            var ids = deleted.Concat(inserted).Select(p => p.Id).ToList();
            var present = (await entries.Find(session, Builders<BsonDocument>.Filter.In("_id", ids)).Project(new BsonDocument("_id", 1)).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(d => d["_id"].AsObjectId).ToHashSet();
            return new AppliedBonusChanges(
                [.. inserted.Where(p => present.Contains(p.Id)).Select(p => p.Key)],
                [.. deleted.Where(p => !present.Contains(p.Id)).Select(p => p.Key)]);
        }
        catch (MongoBulkWriteException e) when (e.WriteErrors.Any(w => w.Category == ServerErrorCategory.DuplicateKey))
        {
            // Inside a transaction a duplicate key aborts it: the attempt reruns and reads the entry that is there now.
            e.AddErrorLabel(TransientLabel);
            throw;
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("apply the bonuses", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<PointTotal>, PortError>> SumByPersonAsync(DateTimeOffset? from, DateTimeOffset? toExclusive, CancellationToken cancellationToken)
    {
        var date = new BsonDocument();
        if (from is { } start)
        {
            date.Add("$gte", new BsonDateTime(start.UtcDateTime));
        }

        if (toExclusive is { } end)
        {
            date.Add("$lt", new BsonDateTime(end.UtcDateTime));
        }

        var bonusKinds = new BsonArray { "bonus_week_done", "bonus_week_ontime", "bonus_cycle_done", "bonus_cycle_ontime" };
        var stages = new[]
        {
            new BsonDocument("$match", date.ElementCount > 0 ? new BsonDocument("date", date) : new BsonDocument()),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", "$personId" },
                { "points", new BsonDocument("$sum", "$amount") },
                { "executions", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$kind", "execution" }), 1, 0 })) },
                { "bonusPoints", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$in", new BsonArray { "$kind", bonusKinds }), "$amount", 0 })) },
                { "redeemed", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$kind", "redemption" }), new BsonDocument("$multiply", new BsonArray { "$amount", -1 }), 0 })) },
            }),
        };
        try
        {
            var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(stages);
            var cursor = MongoTransactionContext.Session is { } session
                ? await entries.AggregateAsync(session, pipeline, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await entries.AggregateAsync(pipeline, cancellationToken: cancellationToken).ConfigureAwait(false);
            var rows = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
            return OneOf<IReadOnlyList<PointTotal>, PortError>.FromT0([.. rows
                .Where(r => r["_id"].IsObjectId)
                .Select(r => new PointTotal(
                    ObjectIdConverter.ToHex(r["_id"].AsObjectId),
                    r["points"].ToInt64(),
                    r["executions"].ToInt32(),
                    r["bonusPoints"].ToInt64(),
                    r["redeemed"].ToInt64()))]);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("sum the entries", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<PointEntry>, PortError>> ListAsync(PointEntryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ObjectIdConverter.TryParse(query.PersonId, out var person))
        {
            return OneOf<IReadOnlyList<PointEntry>, PortError>.FromT0([]);
        }

        var range = new BsonDocument { { "$gte", new BsonDateTime(query.From.UtcDateTime) }, { "$lt", new BsonDateTime(query.ToExclusive.UtcDateTime) } };
        // Rows this application cannot map (an unknown kind, no key) are left out in the query, so the limit counts only rows that are returned.
        var known = new BsonArray(Enum.GetValues<PointEntryKind>().Select(k => PointNames.ToWire(k)));
        var filter = new BsonDocument { { "personId", person }, { "date", range }, { "kind", new BsonDocument("$in", known) }, { "key", new BsonDocument("$type", "string") } };
        if (query.After is { } after && ObjectIdConverter.TryParse(after.Id, out var afterId))
        {
            // Newest date first, then by id: after (date, id) means an older date, or the same date and a later id.
            var cursorDate = new BsonDateTime(after.Date.UtcDateTime);
            filter = new BsonDocument
            {
                { "personId", person },
                { "kind", new BsonDocument("$in", known) },
                { "key", new BsonDocument("$type", "string") },
                { "$and", new BsonArray
                    {
                        new BsonDocument("date", range),
                        new BsonDocument("$or", new BsonArray
                        {
                            new BsonDocument("date", new BsonDocument("$lt", cursorDate)),
                            new BsonDocument { { "date", cursorDate }, { "_id", new BsonDocument("$gt", afterId) } },
                        }),
                    }
                },
            };
        }

        try
        {
            var find = MongoTransactionContext.Session is { } session ? entries.Find(session, filter) : entries.Find(filter);
            var documents = await find.Sort(Sort).Limit(query.Take).ToListAsync(cancellationToken).ConfigureAwait(false);
            return OneOf<IReadOnlyList<PointEntry>, PortError>.FromT0([.. documents.Select(ToEntry).OfType<PointEntry>()]);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("list the entries", e);
        }
    }

    /// <summary>Newest date first, then by id: the order of the Node server and the one the <c>{ personId, date }</c> index serves.</summary>
    private static readonly SortDefinition<BsonDocument> Sort = Builders<BsonDocument>.Sort.Descending("date").Ascending("_id");

    /// <summary>Matches the entry only while it still has the values that were read (compare-and-set).</summary>
    private static BsonDocument? SameAsRead(PointEntry current) =>
        ObjectIdConverter.TryParse(current.Id, out var id) && ObjectIdConverter.TryParse(current.PersonId, out var person)
            ? new BsonDocument
            {
                { "_id", id },
                { "personId", person },
                { "amount", current.Amount },
                { "date", new BsonDateTime(current.Date.UtcDateTime) },
            }
            : null;

    private static BsonDocument SetFields(ExecutionEntryFields fields, PointEntrySource source, DateTimeOffset at) => new()
    {
        { "personId", ObjectIdConverter.Parse(fields.PersonId) },
        { "amount", fields.Amount },
        { "date", new BsonDateTime(fields.Date.UtcDateTime) },
        { "weekStart", new BsonDateTime(fields.WeekStart.UtcDateTime) },
        { "occurrenceId", ObjectIdConverter.Parse(fields.OccurrenceId) },
        { "taskId", fields.TaskId is { } task ? ObjectIdConverter.Parse(task) : BsonNull.Value },
        { "titleSnapshot", fields.TitleSnapshot },
        { "source", PointNames.ToWire(source) },
        { "updatedAt", new BsonDateTime(at.UtcDateTime) },
    };

    private static BsonDocument ToDocument(ObjectId id, string key, ExecutionEntryFields fields, PointEntrySource source, DateTimeOffset at) => new()
    {
        { "_id", id },
        { "key", key },
        { "kind", "execution" },
        { "personId", ObjectIdConverter.Parse(fields.PersonId) },
        { "amount", fields.Amount },
        { "date", new BsonDateTime(fields.Date.UtcDateTime) },
        { "weekStart", new BsonDateTime(fields.WeekStart.UtcDateTime) },
        { "periodStart", BsonNull.Value },
        { "occurrenceId", ObjectIdConverter.Parse(fields.OccurrenceId) },
        { "taskId", fields.TaskId is { } task ? ObjectIdConverter.Parse(task) : BsonNull.Value },
        { "titleSnapshot", fields.TitleSnapshot },
        { "source", PointNames.ToWire(source) },
        { "createdAt", new BsonDateTime(at.UtcDateTime) },
        { "updatedAt", new BsonDateTime(at.UtcDateTime) },
    };

    private static BsonDocument ToDocument(ObjectId id, BonusEntryInsert insert, DateTimeOffset at) => new()
    {
        { "_id", id },
        { "key", insert.Key },
        { "kind", PointNames.ToWire(insert.Kind) },
        { "personId", ObjectIdConverter.Parse(insert.PersonId) },
        { "amount", insert.Amount },
        { "date", new BsonDateTime(insert.Date.UtcDateTime) },
        { "weekStart", new BsonDateTime(insert.WeekStart.UtcDateTime) },
        { "periodStart", new BsonDateTime(insert.PeriodStart.UtcDateTime) },
        { "occurrenceId", BsonNull.Value },
        { "taskId", BsonNull.Value },
        { "titleSnapshot", string.Empty },
        { "source", PointNames.ToWire(PointEntrySource.Recompute) },
        { "createdAt", new BsonDateTime(at.UtcDateTime) },
        { "updatedAt", new BsonDateTime(at.UtcDateTime) },
    };

    /// <summary>An entry of a kind or with a person this application does not know is not mapped.</summary>
    internal static PointEntry? ToEntry(BsonDocument document)
    {
        if (!document.TryGetValue("kind", out var kind) || !kind.IsString || !PointNames.TryParseKind(kind.AsString, out var parsedKind) ||
            Id(document, "personId") is not { } person || !document.TryGetValue("key", out var key) || !key.IsString)
        {
            return null;
        }

        var createdAt = Instant(document, "createdAt") ?? DateTimeOffset.UnixEpoch;
        var date = Instant(document, "date") ?? DateTimeOffset.UnixEpoch;
        return new PointEntry(
            ObjectIdConverter.ToHex(document["_id"].AsObjectId),
            key.AsString,
            parsedKind,
            person,
            Number(document, "amount") ?? 0,
            date,
            Instant(document, "weekStart") ?? date,
            Instant(document, "periodStart"),
            Id(document, "occurrenceId"),
            Id(document, "taskId"),
            Text(document, "titleSnapshot") ?? string.Empty,
            document.TryGetValue("source", out var source) && source.IsString && PointNames.TryParseSource(source.AsString, out var parsedSource) ? parsedSource : PointEntrySource.Live,
            Text(document, "note"),
            Number(document, "centsPerPointSnapshot"),
            Text(document, "currencyCodeSnapshot"),
            createdAt,
            Instant(document, "updatedAt") ?? createdAt);
    }

    private static string? Id(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsObjectId ? ObjectIdConverter.ToHex(value.AsObjectId) : null;

    private static string? Text(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsString ? value.AsString : null;

    private static int? Number(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsNumeric ? value.ToInt32() : null;

    private static DateTimeOffset? Instant(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsValidDateTime
            ? new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
            : null;

    private static int Count(long value) => (int)Math.Min(value, int.MaxValue);

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"pointEntries.failed: could not {operation} ({e.GetType().Name}).");

    private static PortError NoTransaction() =>
        new("pointEntries.no_transaction: a ledger entry can only be written inside a transaction, together with its audit entry.");
}
