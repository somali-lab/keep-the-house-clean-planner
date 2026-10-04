using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Badges;

/// <summary>
/// <see cref="ForStoringBadges"/> on the <c>badges</c> collection. The documents are exactly the ones of the Node server (<c>BadgeDoc</c>: <c>_id, name,
/// description, rule, active, exampleKey, image, createdAt, updatedAt</c>, with the picture as <c>{ data: Binary, contentType, size, hash }</c>), so both
/// applications read each other's badges (ADR-0014). Mapped by hand: the driver types stay in this class. A document that cannot be read (a rule without
/// its task list, an unknown rule type) is a <see cref="PortError"/>, never an exception.
/// </summary>
/// <remarks>
/// Writes enlist in the running transaction and refuse to run without one: a badge change without its audit entry must never exist. Reads join the
/// transaction when there is one, and no read except <see cref="FindImageAsync"/> loads the picture bytes. A transient transaction error propagates so the
/// runner retries the attempt; any other infrastructure failure is a <see cref="PortError"/> without configuration values.
/// </remarks>
internal sealed class MongoBadgeStore : ForStoringBadges
{
    private const string TransientLabel = "TransientTransactionError";
    private const int ListAllCap = 1000;

    private static readonly BsonDocument WithoutBytes = new("image.data", 0);

    private readonly IMongoCollection<BsonDocument> badges;

    public MongoBadgeStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        badges = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Badges);
    }

    public async Task<OneOf<IReadOnlyList<Badge>, PortError>> ListAsync(bool? active, BadgeCursor? after, int take, CancellationToken cancellationToken)
    {
        var filters = new List<BsonDocument>();
        if (active is { } flag)
        {
            filters.Add(new BsonDocument("active", flag));
        }

        if (after is not null && ObjectIdConverter.TryParse(after.Id.ToLowerInvariant(), out var afterId))
        {
            var at = new BsonDateTime(after.CreatedAtMs);
            filters.Add(new BsonDocument("$or", new BsonArray
            {
                new BsonDocument("createdAt", new BsonDocument("$gt", at)),
                new BsonDocument { { "createdAt", at }, { "_id", new BsonDocument("$gt", afterId) } },
            }));
        }

        try
        {
            var documents = await FindFluent(filters.Count == 0 ? new BsonDocument() : new BsonDocument("$and", new BsonArray(filters)))
                .Project(WithoutBytes)
                .Sort(new BsonDocument { { "createdAt", 1 }, { "_id", 1 } })
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

    public async Task<OneOf<IReadOnlyList<Badge>, PortError>> ListAllAsync(bool? active, CancellationToken cancellationToken)
    {
        try
        {
            var documents = await FindFluent(active is { } flag ? new BsonDocument("active", flag) : new BsonDocument())
                .Project(WithoutBytes)
                .Sort(new BsonDocument { { "createdAt", 1 }, { "_id", 1 } })
                .Limit(ListAllCap)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return Read(documents);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("list all", e);
        }
    }

    public async Task<OneOf<Badge, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(id, out var objectId))
        {
            return new NotFound();
        }

        return await FindOneAsync(new BsonDocument("_id", objectId), cancellationToken).ConfigureAwait(false);
    }

    public Task<OneOf<Badge, NotFound, PortError>> FindByExampleKeyAsync(string exampleKey, CancellationToken cancellationToken) =>
        FindOneAsync(new BsonDocument("exampleKey", exampleKey), cancellationToken);

    private async Task<OneOf<Badge, NotFound, PortError>> FindOneAsync(BsonDocument filter, CancellationToken cancellationToken)
    {
        try
        {
            var document = await FindFluent(filter).Project(WithoutBytes).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return new NotFound();
            }

            return TryMap(document, out var badge) ? badge : Unreadable();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("find", e);
        }
    }

    public async Task<OneOf<int, PortError>> CountAsync(CancellationToken cancellationToken)
    {
        try
        {
            var count = MongoTransactionContext.Session is { } session
                ? await badges.CountDocumentsAsync(session, new BsonDocument(), cancellationToken: cancellationToken).ConfigureAwait(false)
                : await badges.CountDocumentsAsync(new BsonDocument(), cancellationToken: cancellationToken).ConfigureAwait(false);
            return (int)Math.Min(count, int.MaxValue);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("count", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<Badge>, PortError>> FindNamingTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(taskId, out var objectId))
        {
            return OneOf<IReadOnlyList<Badge>, PortError>.FromT0([]);
        }

        try
        {
            var documents = await FindFluent(new BsonDocument("rule.taskIds", objectId))
                .Project(WithoutBytes)
                .Sort(new BsonDocument { { "createdAt", 1 }, { "_id", 1 } })
                .Limit(ListAllCap)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return Read(documents);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("find the badges that name a task", e);
        }
    }

    public async Task<OneOf<BadgeImageData, NotFound, PortError>> FindImageAsync(string id, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(id, out var objectId))
        {
            return new NotFound();
        }

        try
        {
            var document = await FindFluent(new BsonDocument("_id", objectId))
                .Project(new BsonDocument("image", 1))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (document is null || !document.TryGetValue("image", out var image) || !image.IsBsonDocument)
            {
                return new NotFound();
            }

            return TryMapImage(image.AsBsonDocument, out var data) ? data : Unreadable();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the image", e);
        }
    }

    public async Task<OneOf<Badge, PortError>> InsertAsync(NewBadge badge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(badge);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        var now = new BsonDateTime(badge.CreatedAt.UtcDateTime);
        var document = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "name", badge.Name },
            { "description", badge.Description },
            { "rule", RuleDocument(badge.Rule) },
            { "active", badge.Active },
            { "exampleKey", badge.ExampleKey is { } key ? key : BsonNull.Value },
            { "image", badge.Image is { } image ? ImageDocument(image) : BsonNull.Value },
            { "createdAt", now },
            { "updatedAt", now },
        };
        try
        {
            await badges.InsertOneAsync(session, document, cancellationToken: cancellationToken).ConfigureAwait(false);
            return TryMap(document, out var inserted) ? inserted : Unreadable();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("insert", e);
        }
    }

    public async Task<OneOf<Badge, NotFound, PortError>> UpdateAsync(string id, BadgeChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (!ObjectIdConverter.TryParse(id, out var objectId))
        {
            return new NotFound();
        }

        var set = new BsonDocument();
        if (changes.Name is not null)
        {
            set.Add("name", changes.Name);
        }

        if (changes.Description is not null)
        {
            set.Add("description", changes.Description);
        }

        if (changes.Rule is not null)
        {
            set.Add("rule", RuleDocument(changes.Rule));
        }

        if (changes.Active is { } active)
        {
            set.Add("active", active);
        }

        if (changes.Image is { } image)
        {
            set.Add("image", ImageDocument(image));
        }
        else if (changes.ClearImage)
        {
            set.Add("image", BsonNull.Value);
        }

        set.Add("updatedAt", new BsonDateTime(updatedAt.UtcDateTime));
        try
        {
            var document = await badges.FindOneAndUpdateAsync(
                session,
                new BsonDocument("_id", objectId),
                new BsonDocument("$set", set),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After, Projection = WithoutBytes },
                cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return new NotFound();
            }

            return TryMap(document, out var updated) ? updated : Unreadable();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("update", e);
        }
    }

    public async Task<OneOf<Success, NotFound, PortError>> DeleteAsync(string id, CancellationToken cancellationToken)
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
            var result = await badges.DeleteOneAsync(session, new BsonDocument("_id", objectId), cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.DeletedCount == 1 ? new Success() : new NotFound();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("delete", e);
        }
    }

    private IFindFluent<BsonDocument, BsonDocument> FindFluent(BsonDocument filter) =>
        MongoTransactionContext.Session is { } session ? badges.Find(session, filter) : badges.Find(filter);

    // ---- mapping

    private static BsonDocument RuleDocument(BadgeRule rule) => rule.Type == BadgeRuleType.OnTimeWeeks
        ? new BsonDocument { { "type", BadgeNames.ToWire(rule.Type) }, { "threshold", rule.Threshold } }
        : new BsonDocument
        {
            { "type", BadgeNames.ToWire(rule.Type) },
            { "taskIds", new BsonArray(BadgeValidation.SortedIds(rule.TaskIds).Select(id => (BsonValue)ObjectIdConverter.Parse(id))) },
            { "threshold", rule.Threshold },
        };

    private static BsonDocument ImageDocument(BadgeImageData image) => new()
    {
        { "data", new BsonBinaryData(image.Bytes) },
        { "contentType", image.ContentType.ContentType() },
        { "size", image.Size },
        { "hash", image.Hash },
    };

    private static OneOf<IReadOnlyList<Badge>, PortError> Read(List<BsonDocument> documents)
    {
        var result = new List<Badge>(documents.Count);
        foreach (var document in documents)
        {
            if (!TryMap(document, out var badge))
            {
                return Unreadable();
            }

            result.Add(badge);
        }

        return result;
    }

    /// <summary>A badge from its document; <see langword="false"/> when the document is not the shape the application writes.</summary>
    private static bool TryMap(BsonDocument document, out Badge badge)
    {
        badge = null!;
        try
        {
            var rule = document["rule"].AsBsonDocument;
            if (!BadgeNames.TryParseRuleType(rule["type"].AsString, out var type))
            {
                return false;
            }

            var threshold = rule["threshold"].ToInt32();
            var mapped = type == BadgeRuleType.OnTimeWeeks
                ? BadgeRule.OnTimeWeeks(threshold)
                : new BadgeRule(type, BadgeValidation.SortedIds(rule["taskIds"].AsBsonArray.Select(v => ObjectIdConverter.ToHex(v.AsObjectId))), threshold);
            BadgeImageInfo? image = null;
            if (document.TryGetValue("image", out var imageValue) && imageValue.IsBsonDocument)
            {
                var info = imageValue.AsBsonDocument;
                if (!BadgeNames.TryParseImageType(info["contentType"].AsString, out var imageType))
                {
                    return false;
                }

                image = new BadgeImageInfo(imageType, info["size"].ToInt32(), info["hash"].AsString);
            }

            var createdAt = Instant(document["createdAt"]);
            badge = new Badge(
                ObjectIdConverter.ToHex(document["_id"].AsObjectId),
                document["name"].AsString,
                document.GetValue("description", string.Empty).AsString,
                mapped,
                document["active"].ToBoolean(),
                document.TryGetValue("exampleKey", out var key) && key.IsString ? key.AsString : null,
                image,
                createdAt,
                document.TryGetValue("updatedAt", out var updated) ? Instant(updated) : createdAt);
            return true;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidCastException or FormatException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryMapImage(BsonDocument image, out BadgeImageData data)
    {
        data = null!;
        try
        {
            if (!BadgeNames.TryParseImageType(image["contentType"].AsString, out var type))
            {
                return false;
            }

            data = new BadgeImageData(image["data"].AsBsonBinaryData.Bytes, type, image["hash"].AsString);
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
        new($"badges.failed: could not {operation} ({e.GetType().Name}).");

    private static PortError Unreadable() =>
        new("badges.unreadable: a stored badge is not in the shape the application writes (rule, task list or image).");

    private static PortError NoTransaction() =>
        new("badges.no_transaction: a badge can only be written inside a transaction, together with its audit entry.");
}
