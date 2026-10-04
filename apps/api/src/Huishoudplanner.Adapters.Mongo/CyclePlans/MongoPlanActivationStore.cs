using Huishoudplanner.Adapters.Mongo.Occurrences;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.CyclePlans;

/// <summary>
/// <see cref="ForActivatingCyclePlans"/> and <see cref="ForReadingOccurrencesForActivation"/>. The activation writes the plans exactly as the
/// Node server does (<c>setActivePlan</c>: other active plans get <c>active: false</c>, the plan <c>active: true</c>, both with a new
/// <c>updatedAt</c>), and the guard is the <c>activationVersion</c> counter on the singleton <c>settings</c> document: a field the Node server
/// does not know and ignores. Writes enlist in the running transaction and refuse to run without one. A transient transaction error (a write
/// conflict on the guard included) propagates so the runner retries the attempt; any other infrastructure failure is a <see cref="PortError"/>.
/// </summary>
internal sealed class MongoPlanActivationStore : ForActivatingCyclePlans, ForReadingOccurrencesForActivation
{
    internal const string GuardField = "activationVersion";

    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> plans;
    private readonly IMongoCollection<BsonDocument> settings;
    private readonly IMongoCollection<BsonDocument> occurrences;

    public MongoPlanActivationStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        var database = MongoClientFactory.GetDatabase(client, options);
        plans = database.GetCollection<BsonDocument>(MongoCollections.CyclePlans);
        settings = database.GetCollection<BsonDocument>(MongoCollections.Settings);
        occurrences = database.GetCollection<BsonDocument>(MongoCollections.Occurrences);
    }

    public async Task<OneOf<Success, SettingsMissing, PortError>> TouchGuardAsync(CancellationToken cancellationToken)
    {
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        try
        {
            var result = await settings.UpdateOneAsync(
                session,
                new BsonDocument("_id", SettingsDocument.SingletonId),
                new BsonDocument("$inc", new BsonDocument(GuardField, 1)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.MatchedCount == 1 ? new Success() : new SettingsMissing();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("write the activation guard", e);
        }
    }

    public async Task<OneOf<PlanActivation, NotFound, PortError>> ActivateAsync(string planId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (!ObjectIdConverter.TryParse(planId, out var id))
        {
            return new NotFound();
        }

        var now = new BsonDateTime(at.UtcDateTime);
        try
        {
            var beforeDocument = await plans.Find(session, new BsonDocument("_id", id)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (beforeDocument is null)
            {
                return new NotFound();
            }

            var others = await plans
                .Find(session, new BsonDocument { { "active", true }, { "_id", new BsonDocument("$ne", id) } })
                .Project(Builders<BsonDocument>.Projection.Include("_id"))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var otherIds = others.ConvertAll(d => d["_id"].AsObjectId);
            if (otherIds.Count > 0)
            {
                await plans.UpdateManyAsync(
                    session,
                    new BsonDocument("_id", new BsonDocument("$in", new BsonArray(otherIds))),
                    EntityVersioning.Raise(new BsonDocument("$set", new BsonDocument { { "active", false }, { "updatedAt", now } })),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var set = new BsonDocument { { "active", true }, { "updatedAt", now } };
            if (beforeDocument.GetValue("draft", false).ToBoolean())
            {
                set.Add("draft", false);
            }

            var afterDocument = await plans.FindOneAndUpdateAsync(
                session,
                new BsonDocument("_id", id),
                EntityVersioning.Raise(new BsonDocument("$set", set)),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
                cancellationToken).ConfigureAwait(false);
            if (afterDocument is null)
            {
                return new NotFound();
            }

            return new PlanActivation(
                MongoCyclePlanStore.ToPlan(beforeDocument),
                MongoCyclePlanStore.ToPlan(afterDocument),
                [.. otherIds.Select(ObjectIdConverter.ToHex)]);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("activate", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<Occurrence>, PortError>> FindForActivationAsync(
        DateTimeOffset rangeStart, DateTimeOffset rangeEnd, IReadOnlyCollection<string> cycleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycleIds);
        var range = new BsonDocument { { "$gte", new BsonDateTime(rangeStart.UtcDateTime) }, { "$lt", new BsonDateTime(rangeEnd.UtcDateTime) } };
        var any = new BsonArray { new BsonDocument("date", range), new BsonDocument("plannedDate", range) };
        var ids = cycleIds.Where(id => ObjectIdConverter.TryParse(id, out _)).Select(ObjectIdConverter.Parse).ToList();
        if (ids.Count > 0)
        {
            any.Add(new BsonDocument("cycleId", new BsonDocument("$in", new BsonArray(ids))));
        }

        try
        {
            var filter = new BsonDocument("$or", any);
            var find = MongoTransactionContext.Session is { } session ? occurrences.Find(session, filter) : occurrences.Find(filter);
            var documents = await find.ToListAsync(cancellationToken).ConfigureAwait(false);
            return OneOf<IReadOnlyList<Occurrence>, PortError>.FromT0(documents.ConvertAll(MongoOccurrenceStore.ToOccurrence));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the occurrences of the activation window", e);
        }
    }

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"cycle_plans.failed: could not {operation} ({e.GetType().Name}).");

    private static PortError NoTransaction() =>
        new("cycle_plans.no_transaction: a plan can only be activated inside a transaction, together with its audit entry.");
}
