using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Badges;

/// <summary>
/// <see cref="ForStoringBadgeAwards"/> on the <c>badgeAwards</c> collection of the Node server (<c>BadgeAwardDoc</c>: <c>_id, key, badgeId, personId, awardedAt,
/// createdAt, updatedAt</c>; the unique index on <c>key</c> makes a double award impossible). Mapped by hand: the driver types stay in this class.
/// </summary>
/// <remarks>
/// <see cref="ApplyAsync"/> enlists in the running transaction and refuses to run without one. Reads join the transaction when there is one. A document
/// that cannot be read is a <see cref="PortError"/>. A transient transaction error propagates so the runner retries the attempt.
/// </remarks>
internal sealed class MongoBadgeAwardStore : ForStoringBadgeAwards
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> awards;

    public MongoBadgeAwardStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        awards = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.BadgeAwards);
    }

    public async Task<OneOf<IReadOnlyList<BadgeAward>, PortError>> ListAsync(BadgeAwardQuery query, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filters = new List<BsonDocument>();
        if (query.PersonId is { } person)
        {
            if (!ObjectIdConverter.TryParse(person, out var personId))
            {
                return OneOf<IReadOnlyList<BadgeAward>, PortError>.FromT0([]);
            }

            filters.Add(new BsonDocument("personId", personId));
        }

        if (query.After is { } after && ObjectIdConverter.TryParse(after.Id.ToLowerInvariant(), out var afterId))
        {
            var at = new BsonDateTime(after.AwardedAtMs);
            filters.Add(new BsonDocument("$or", new BsonArray
            {
                new BsonDocument("awardedAt", new BsonDocument("$gt", at)),
                new BsonDocument { { "awardedAt", at }, { "_id", new BsonDocument("$gt", afterId) } },
            }));
        }

        try
        {
            var documents = await FindFluent(filters.Count == 0 ? new BsonDocument() : new BsonDocument("$and", new BsonArray(filters)))
                .Sort(new BsonDocument { { "awardedAt", 1 }, { "_id", 1 } })
                .Limit(take)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return Read(documents);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("list", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<BadgeAward>, PortError>> FindForEvaluationAsync(IReadOnlyCollection<string>? personIds, CancellationToken cancellationToken)
    {
        var filter = new BsonDocument();
        if (personIds is not null)
        {
            var ids = personIds.Where(id => ObjectIdConverter.TryParse(id, out _)).Select(ObjectIdConverter.Parse).ToList();
            if (ids.Count == 0)
            {
                return OneOf<IReadOnlyList<BadgeAward>, PortError>.FromT0([]);
            }

            filter.Add("personId", new BsonDocument("$in", new BsonArray(ids)));
        }

        try
        {
            var documents = await FindFluent(filter).ToListAsync(cancellationToken).ConfigureAwait(false);
            return Read(documents);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the awards", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<AppliedAward>, PortError>> ApplyAsync(BadgeAwardPlan plan, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        var applied = new List<AppliedAward>();
        var now = new BsonDateTime(at.UtcDateTime);
        try
        {
            foreach (var current in plan.Deletes)
            {
                var deleted = await awards.FindOneAndDeleteAsync(session, Current(current), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (deleted is not null && TryMap(deleted, out var award))
                {
                    applied.Add(new AppliedAward(AwardChange.Removed, award));
                }
            }

            foreach (var move in plan.Updates)
            {
                var after = await awards.FindOneAndUpdateAsync(
                    session,
                    Current(move.Current),
                    new BsonDocument("$set", new BsonDocument { { "awardedAt", new BsonDateTime(move.AwardedAt.UtcDateTime) }, { "updatedAt", now } }),
                    new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
                    cancellationToken).ConfigureAwait(false);
                if (after is not null && TryMap(after, out var award))
                {
                    applied.Add(new AppliedAward(AwardChange.Updated, award, move.Current));
                }
            }

            foreach (var insert in plan.Inserts)
            {
                var document = new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() },
                    { "key", BadgeAward.KeyOf(insert.BadgeId, insert.PersonId) },
                    { "badgeId", ObjectIdConverter.Parse(insert.BadgeId) },
                    { "personId", ObjectIdConverter.Parse(insert.PersonId) },
                    { "awardedAt", new BsonDateTime(insert.AwardedAt.UtcDateTime) },
                    { "createdAt", now },
                    { "updatedAt", now },
                };
                await awards.InsertOneAsync(session, document, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (TryMap(document, out var award))
                {
                    applied.Add(new AppliedAward(AwardChange.Created, award));
                }
            }
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("apply the awards", e);
        }

        return applied;
    }

    /// <summary>The award as it was read: a move or a removal only applies while its moment is unchanged (compare-and-set).</summary>
    private static BsonDocument Current(BadgeAward award) => new()
    {
        { "_id", ObjectIdConverter.Parse(award.Id) },
        { "awardedAt", new BsonDateTime(award.AwardedAt.UtcDateTime) },
    };

    private IFindFluent<BsonDocument, BsonDocument> FindFluent(BsonDocument filter) =>
        MongoTransactionContext.Session is { } session ? awards.Find(session, filter) : awards.Find(filter);

    private static OneOf<IReadOnlyList<BadgeAward>, PortError> Read(List<BsonDocument> documents)
    {
        var result = new List<BadgeAward>(documents.Count);
        foreach (var document in documents)
        {
            if (!TryMap(document, out var award))
            {
                return new PortError("badgeAwards.unreadable: a stored award is not in the shape the application writes.");
            }

            result.Add(award);
        }

        return result;
    }

    private static bool TryMap(BsonDocument document, out BadgeAward award)
    {
        award = null!;
        try
        {
            var createdAt = Instant(document["createdAt"]);
            award = new BadgeAward(
                ObjectIdConverter.ToHex(document["_id"].AsObjectId),
                document["key"].AsString,
                ObjectIdConverter.ToHex(document["badgeId"].AsObjectId),
                ObjectIdConverter.ToHex(document["personId"].AsObjectId),
                Instant(document["awardedAt"]),
                createdAt,
                document.TryGetValue("updatedAt", out var updated) ? Instant(updated) : createdAt);
            return true;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidCastException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static DateTimeOffset Instant(BsonValue value) =>
        value.IsValidDateTime ? new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.UnixEpoch;

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"badgeAwards.failed: could not {operation} ({e.GetType().Name}).");

    private static PortError NoTransaction() =>
        new("badgeAwards.no_transaction: awards can only be written inside a transaction, together with their audit entries.");
}
