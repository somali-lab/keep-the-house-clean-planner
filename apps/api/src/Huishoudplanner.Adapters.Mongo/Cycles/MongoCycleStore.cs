using System.Globalization;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Cycles;

/// <summary>
/// <see cref="ForStoringCycles"/> on the <c>cycles</c> collection. The documents are exactly the ones of the Node server (<c>CycleDoc</c>:
/// <c>_id, index, startDate, endDate, planId, generatedAt, generationRunId</c> with the dates as <c>YYYY-MM-DD</c> strings), so both
/// applications read each other's cycles; the unique index on <c>index</c> keeps one document per cycle. Mapped by hand: the driver types
/// stay in this class.
/// </summary>
/// <remarks>
/// Writes enlist in the running transaction (<see cref="MongoTransactionContext.Session"/>) and refuse to run without one: a cycle change
/// without its audit entry must never exist. Reads join the transaction when there is one. A transient transaction error propagates so the
/// runner retries the attempt; any other infrastructure failure is a <see cref="PortError"/> without configuration values.
/// </remarks>
internal sealed class MongoCycleStore : ForStoringCycles
{
    private const string TransientLabel = "TransientTransactionError";

    private const string DayFormat = "yyyy-MM-dd";

    private readonly IMongoCollection<BsonDocument> cycles;

    public MongoCycleStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        cycles = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Cycles);
    }

    public async Task<OneOf<IReadOnlyList<Cycle>, PortError>> ListAsync(CycleCursor? after, int take, CancellationToken cancellationToken)
    {
        var filter = after is null ? FilterDefinition<BsonDocument>.Empty : new BsonDocument("index", new BsonDocument("$gt", after.Index));
        try
        {
            var documents = await FindFluent(filter).Sort(Builders<BsonDocument>.Sort.Ascending("index")).Limit(take).ToListAsync(cancellationToken).ConfigureAwait(false);
            return OneOf<IReadOnlyList<Cycle>, PortError>.FromT0(documents.ConvertAll(ToCycle));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("list", e);
        }
    }

    public async Task<OneOf<Cycle, NotFound, PortError>> FindByIndexAsync(int index, CancellationToken cancellationToken)
    {
        try
        {
            var document = await FindFluent(new BsonDocument("index", index)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null ? new NotFound() : ToCycle(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("find", e);
        }
    }

    public async Task<OneOf<Cycle, PortError>> InsertAsync(NewCycle cycle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        var document = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "index", cycle.Index },
            { "startDate", Day(cycle.StartDate) },
            { "endDate", Day(cycle.EndDate) },
            { "planId", cycle.PlanId is { } plan ? ObjectIdConverter.Parse(plan) : BsonNull.Value },
            { "generatedAt", new BsonDateTime(cycle.GeneratedAt.UtcDateTime) },
            { "generationRunId", cycle.GenerationRunId },
        };
        try
        {
            await cycles.InsertOneAsync(session, document, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ToCycle(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("insert", e);
        }
    }

    public Task<OneOf<Cycle, NotFound, PortError>> UpdateBoundsAsync(string id, DateOnly startDate, DateOnly endDate, DateTimeOffset generatedAt, string runId, CancellationToken cancellationToken) =>
        UpdateAsync(
            id,
            new BsonDocument
            {
                { "startDate", Day(startDate) },
                { "endDate", Day(endDate) },
                { "generatedAt", new BsonDateTime(generatedAt.UtcDateTime) },
                { "generationRunId", runId },
            },
            cancellationToken);

    public Task<OneOf<Cycle, NotFound, PortError>> UpdatePlanAsync(string id, string planId, DateTimeOffset generatedAt, string runId, CancellationToken cancellationToken) =>
        UpdateAsync(
            id,
            new BsonDocument
            {
                { "planId", ObjectIdConverter.Parse(planId) },
                { "generatedAt", new BsonDateTime(generatedAt.UtcDateTime) },
                { "generationRunId", runId },
            },
            cancellationToken);

    private async Task<OneOf<Cycle, NotFound, PortError>> UpdateAsync(string id, BsonDocument set, CancellationToken cancellationToken)
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
            var document = await cycles.FindOneAndUpdateAsync(
                session,
                new BsonDocument("_id", objectId),
                new BsonDocument("$set", set),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
                cancellationToken).ConfigureAwait(false);
            return document is null ? new NotFound() : ToCycle(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("update", e);
        }
    }

    private IFindFluent<BsonDocument, BsonDocument> FindFluent(FilterDefinition<BsonDocument> filter) =>
        MongoTransactionContext.Session is { } session ? cycles.Find(session, filter) : cycles.Find(filter);

    private static string Day(DateOnly day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);

    private static Cycle ToCycle(BsonDocument document)
    {
        var generatedAt = document.TryGetValue("generatedAt", out var at) && at.IsValidDateTime
            ? new DateTimeOffset(at.ToUniversalTime(), TimeSpan.Zero)
            : DateTimeOffset.UnixEpoch;
        return new Cycle(
            ObjectIdConverter.ToHex(document["_id"].AsObjectId),
            document["index"].ToInt32(),
            ParseDay(document, "startDate"),
            ParseDay(document, "endDate"),
            document.TryGetValue("planId", out var plan) && plan.IsObjectId ? ObjectIdConverter.ToHex(plan.AsObjectId) : null,
            generatedAt,
            document.TryGetValue("generationRunId", out var run) && run.IsString ? run.AsString : string.Empty);
    }

    private static DateOnly ParseDay(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsString
            && DateOnly.TryParseExact(value.AsString, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : DateOnly.MinValue;

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"cycles.failed: could not {operation} ({e.GetType().Name}).");

    private static PortError NoTransaction() =>
        new("cycles.no_transaction: a cycle can only be written inside a transaction, together with its audit entry.");
}
