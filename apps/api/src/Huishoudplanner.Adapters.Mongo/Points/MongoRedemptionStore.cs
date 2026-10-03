using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Points;

/// <summary>
/// <see cref="ForStoringRedemptions"/> on the <c>pointEntries</c> collection: the booked entries of kind <c>redemption</c>, exactly the documents of
/// the Node server (<c>RedemptionDoc</c>, requirements 3), so both applications read each other's bookings. The partial unique index
/// <c>pointEntries_request_id_unique</c> makes a request key idempotent: a duplicate key is reported as <see cref="RequestKeyTaken"/>, and inside a
/// transaction the server then abandons the transaction, so the caller rolls back and starts again.
/// </summary>
/// <remarks>
/// The guard of the balance is one document per person in <see cref="MongoCollections.PointGuards"/>, created on the first booking (an upsert; two
/// first bookings racing for the insert fail with a duplicate key, which is labelled transient so that the runner reruns the loser). Writes enlist
/// in the running transaction and refuse to run without one. A transient transaction error propagates so the runner retries the attempt; any other
/// infrastructure failure is a <see cref="PortError"/> without configuration values.
/// </remarks>
internal sealed class MongoRedemptionStore : ForStoringRedemptions
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> entries;
    private readonly IMongoCollection<BsonDocument> guards;

    public MongoRedemptionStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        var database = MongoClientFactory.GetDatabase(client, options);
        entries = database.GetCollection<BsonDocument>(MongoCollections.PointEntries);
        guards = database.GetCollection<BsonDocument>(MongoCollections.PointGuards);
    }

    public async Task<OneOf<PointEntry, NotFound, PortError>> FindByRequestIdAsync(string requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        return await FindOneAsync(new BsonDocument { { "requestId", requestId }, { "kind", "redemption" } }, "find a redemption by request key", cancellationToken).ConfigureAwait(false);
    }

    public async Task<OneOf<PointEntry, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(id, out var objectId))
        {
            return new NotFound();
        }

        return await FindOneAsync(new BsonDocument { { "_id", objectId }, { "kind", "redemption" } }, "find a redemption", cancellationToken).ConfigureAwait(false);
    }

    public async Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filter = new BsonDocument("kind", "redemption");
            return MongoTransactionContext.Session is { } session
                ? await entries.CountDocumentsAsync(session, filter, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await entries.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("count the redemptions", e);
        }
    }

    public async Task<OneOf<long, PortError>> BalanceOfAsync(string personId, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(personId, out var person))
        {
            return 0L;
        }

        try
        {
            var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(
            [
                new BsonDocument("$match", new BsonDocument("personId", person)),
                new BsonDocument("$group", new BsonDocument { { "_id", BsonNull.Value }, { "points", new BsonDocument("$sum", "$amount") } }),
            ]);
            var cursor = MongoTransactionContext.Session is { } session
                ? await entries.AggregateAsync(session, pipeline, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await entries.AggregateAsync(pipeline, cancellationToken: cancellationToken).ConfigureAwait(false);
            var rows = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Count == 0 ? 0L : rows[0]["points"].ToInt64();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("sum the balance of a person", e);
        }
    }

    public async Task<OneOf<Success, PortError>> LockBalanceAsync(string personId, CancellationToken cancellationToken)
    {
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (!ObjectIdConverter.TryParse(personId, out var person))
        {
            return new PortError("redemptions.failed: the person of a guard is no id.");
        }

        try
        {
            await guards.UpdateOneAsync(
                session,
                new BsonDocument("_id", person),
                new BsonDocument("$inc", new BsonDocument("version", 1)),
                new UpdateOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
            return new Success();
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Two first bookings of one person raced for the insert of the guard: the loser reruns and then writes the existing document.
            e.AddErrorLabel(TransientLabel);
            throw;
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("write the balance guard", e);
        }
    }

    public async Task<OneOf<PointEntry, RequestKeyTaken, PortError>> InsertAsync(NewRedemption draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        var id = ObjectId.GenerateNewId();
        var at = new BsonDateTime(draft.At.UtcDateTime);
        var document = new BsonDocument
        {
            { "_id", id },
            { "key", "redemption:" + ObjectIdConverter.ToHex(id) },
            { "kind", "redemption" },
            { "personId", ObjectIdConverter.Parse(draft.PersonId) },
            { "amount", -draft.Points },
            { "date", new BsonDateTime(draft.Date.UtcDateTime) },
            { "weekStart", new BsonDateTime(draft.WeekStart.UtcDateTime) },
            { "periodStart", BsonNull.Value },
            { "occurrenceId", BsonNull.Value },
            { "taskId", BsonNull.Value },
            { "titleSnapshot", string.Empty },
            { "source", "live" },
            { "note", draft.Note is { } note ? note : BsonNull.Value },
            { "centsPerPointSnapshot", draft.CentsPerPoint },
            { "currencyCodeSnapshot", draft.CurrencyCode },
            { "requestId", draft.RequestId is { } requestId ? requestId : BsonNull.Value },
            { "createdAt", at },
            { "updatedAt", at },
        };
        try
        {
            await entries.InsertOneAsync(session, document, cancellationToken: cancellationToken).ConfigureAwait(false);
            return MongoPointEntryStore.ToEntry(document)!;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new RequestKeyTaken();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("insert a redemption", e);
        }
    }

    public async Task<OneOf<PointEntry, NotFound, PortError>> DeleteAsync(string id, CancellationToken cancellationToken)
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
            var deleted = await entries.FindOneAndDeleteAsync(
                session,
                new BsonDocument { { "_id", objectId }, { "kind", "redemption" } },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return deleted is not null && MongoPointEntryStore.ToEntry(deleted) is { } entry ? entry : new NotFound();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("delete a redemption", e);
        }
    }

    private async Task<OneOf<PointEntry, NotFound, PortError>> FindOneAsync(BsonDocument filter, string operation, CancellationToken cancellationToken)
    {
        try
        {
            var find = MongoTransactionContext.Session is { } session ? entries.Find(session, filter) : entries.Find(filter);
            var document = await find.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is not null && MongoPointEntryStore.ToEntry(document) is { } entry ? entry : new NotFound();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed(operation, e);
        }
    }

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"redemptions.failed: could not {operation} ({e.GetType().Name}).");

    private static PortError NoTransaction() =>
        new("redemptions.no_transaction: a redemption can only be written inside a transaction, together with its audit entry.");
}
