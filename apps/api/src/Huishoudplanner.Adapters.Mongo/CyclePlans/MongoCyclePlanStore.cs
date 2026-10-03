using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.CyclePlans;

/// <summary>
/// <see cref="ForStoringCyclePlans"/> on the <c>cyclePlans</c> collection. The documents are exactly the ones of the Node server
/// (<c>CyclePlanDoc</c>: <c>_id, name, active, slots[{taskId, weekIndex, weekday, assigneeId, sortOrder}], weekThemes, draft, source,
/// proposalId, rationale, discarded, createdAt, updatedAt</c>), so both applications read each other's plans. Mapped by hand: the driver
/// types stay in this class.
/// </summary>
/// <remarks>
/// Writes enlist in the running transaction (<see cref="MongoTransactionContext.Session"/>) and refuse to run without one: a plan change
/// without its audit entry must never exist. Reads join the transaction when there is one. A transient transaction error propagates so
/// the runner retries the attempt; any other infrastructure failure is a <see cref="PortError"/> without configuration values.
/// </remarks>
internal sealed class MongoCyclePlanStore : ForStoringCyclePlans
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> plans;

    public MongoCyclePlanStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        plans = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.CyclePlans);
    }

    public async Task<OneOf<IReadOnlyList<CyclePlan>, PortError>> ListAsync(CyclePlanCursor? after, int take, CancellationToken cancellationToken)
    {
        var filter = after is null ? FilterDefinition<BsonDocument>.Empty : AfterFilter(after);
        try
        {
            var documents = await FindFluent(filter).Sort(ListOrder).Limit(take).ToListAsync(cancellationToken).ConfigureAwait(false);
            return OneOf<IReadOnlyList<CyclePlan>, PortError>.FromT0(documents.ConvertAll(ToPlan));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("list", e);
        }
    }

    public Task<OneOf<CyclePlan, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken) =>
        !ObjectIdConverter.TryParse(id, out var objectId)
            ? Task.FromResult<OneOf<CyclePlan, NotFound, PortError>>(new NotFound())
            : FindOneAsync(new BsonDocument("_id", objectId), null, "find", cancellationToken);

    public Task<OneOf<CyclePlan, NotFound, PortError>> FindActiveAsync(CancellationToken cancellationToken) =>
        FindOneAsync(new BsonDocument("active", true), null, "find the active plan", cancellationToken);

    public Task<OneOf<CyclePlan, NotFound, PortError>> FindDefaultAsync(CancellationToken cancellationToken) =>
        FindOneAsync(FilterDefinition<BsonDocument>.Empty, ListOrder, "find the default plan", cancellationToken);

    public async Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filter = FilterDefinition<BsonDocument>.Empty;
            return MongoTransactionContext.Session is { } session
                ? await plans.CountDocumentsAsync(session, filter, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await plans.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("count", e);
        }
    }

    public async Task<OneOf<CyclePlan, PortError>> InsertAsync(NewCyclePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        var now = new BsonDateTime(plan.CreatedAt.UtcDateTime);
        var document = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "name", plan.Name },
            { "active", plan.Active },
            { "slots", ToBson(plan.Slots) },
            { "weekThemes", new BsonArray(plan.WeekThemes) },
            { "draft", false },
            { "source", PlanSources.Manual },
            { "proposalId", BsonNull.Value },
            { "rationale", BsonNull.Value },
            { "discarded", false },
            { "createdAt", now },
            { "updatedAt", now },
        };
        try
        {
            await plans.InsertOneAsync(session, document, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ToPlan(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("insert", e);
        }
    }

    public async Task<OneOf<CyclePlan, NotFound, PortError>> UpdateMetaAsync(string id, PlanMetaChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var set = new BsonDocument();
        if (changes.Name is not null)
        {
            set.Add("name", changes.Name);
        }

        if (changes.WeekThemes is not null)
        {
            set.Add("weekThemes", new BsonArray(changes.WeekThemes));
        }

        return await UpdateAsync(id, set, updatedAt, cancellationToken).ConfigureAwait(false);
    }

    public Task<OneOf<CyclePlan, NotFound, PortError>> ReplaceSlotsAsync(string id, IReadOnlyList<CyclePlanSlot> slots, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return UpdateAsync(id, new BsonDocument("slots", ToBson(slots)), updatedAt, cancellationToken);
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
            var result = await plans.DeleteOneAsync(session, new BsonDocument("_id", objectId), cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.DeletedCount == 1 ? new Success() : new NotFound();
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("delete", e);
        }
    }

    private async Task<OneOf<CyclePlan, NotFound, PortError>> UpdateAsync(string id, BsonDocument set, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (MongoTransactionContext.Session is not { IsInTransaction: true } session)
        {
            return NoTransaction();
        }

        if (!ObjectIdConverter.TryParse(id, out var objectId))
        {
            return new NotFound();
        }

        set.Add("updatedAt", new BsonDateTime(updatedAt.UtcDateTime));
        try
        {
            var document = await plans.FindOneAndUpdateAsync(
                session,
                new BsonDocument("_id", objectId),
                new BsonDocument("$set", set),
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
                cancellationToken).ConfigureAwait(false);
            return document is null ? new NotFound() : ToPlan(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("update", e);
        }
    }

    private async Task<OneOf<CyclePlan, NotFound, PortError>> FindOneAsync(FilterDefinition<BsonDocument> filter, SortDefinition<BsonDocument>? sort, string operation, CancellationToken cancellationToken)
    {
        try
        {
            var find = FindFluent(filter);
            var document = await (sort is null ? find : find.Sort(sort)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null ? new NotFound() : ToPlan(document);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed(operation, e);
        }
    }

    private static SortDefinition<BsonDocument> ListOrder => Builders<BsonDocument>.Sort.Ascending("createdAt").Ascending("_id");

    private IFindFluent<BsonDocument, BsonDocument> FindFluent(FilterDefinition<BsonDocument> filter) =>
        MongoTransactionContext.Session is { } session ? plans.Find(session, filter) : plans.Find(filter);

    /// <summary>Plans after the cursor in the order (createdAt, _id).</summary>
    private static BsonDocument AfterFilter(CyclePlanCursor after)
    {
        var createdAt = new BsonDateTime(after.CreatedAt.UtcDateTime);
        return new BsonDocument(
            "$or",
            new BsonArray
            {
                new BsonDocument("createdAt", new BsonDocument("$gt", createdAt)),
                new BsonDocument { { "createdAt", createdAt }, { "_id", new BsonDocument("$gt", ObjectIdConverter.Parse(after.Id)) } },
            });
    }

    private static BsonArray ToBson(IEnumerable<CyclePlanSlot> slots) => new(slots.Select(slot => new BsonDocument
    {
        { "taskId", ObjectIdConverter.Parse(slot.TaskId) },
        { "weekIndex", slot.WeekIndex },
        { "weekday", slot.Weekday },
        { "assigneeId", slot.AssigneeId is { } assignee ? ObjectIdConverter.Parse(assignee) : BsonNull.Value },
        { "sortOrder", slot.SortOrder },
    }));

    private static CyclePlan ToPlan(BsonDocument document)
    {
        var createdAt = Instant(document, "createdAt") ?? DateTimeOffset.UnixEpoch;
        var slots = document.TryGetValue("slots", out var slotValue) && slotValue.IsBsonArray
            ? slotValue.AsBsonArray.Where(s => s.IsBsonDocument).Select(s => ToSlot(s.AsBsonDocument)).ToList()
            : [];
        return new CyclePlan(
            ObjectIdConverter.ToHex(document["_id"].AsObjectId),
            document.GetValue("name", string.Empty).AsString,
            document.GetValue("active", false).ToBoolean(),
            slots,
            Strings(document, "weekThemes") ?? CyclePlanRules.EmptyWeekThemes,
            document.GetValue("draft", false).ToBoolean(),
            document.TryGetValue("source", out var source) && source.IsString ? source.AsString : PlanSources.Manual,
            document.TryGetValue("proposalId", out var proposal) && proposal.IsString ? proposal.AsString : null,
            Strings(document, "rationale"),
            document.GetValue("discarded", false).ToBoolean(),
            createdAt,
            Instant(document, "updatedAt") ?? createdAt);
    }

    private static CyclePlanSlot ToSlot(BsonDocument slot) => new(
        ObjectIdConverter.ToHex(slot["taskId"].AsObjectId),
        slot.GetValue("weekIndex", 0).ToInt32(),
        slot.GetValue("weekday", 0).ToInt32(),
        slot.TryGetValue("assigneeId", out var assignee) && assignee.IsObjectId ? ObjectIdConverter.ToHex(assignee.AsObjectId) : null,
        slot.TryGetValue("sortOrder", out var order) && order.IsNumeric ? order.ToInt32() : 0);

    private static List<string>? Strings(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsBsonArray
            ? value.AsBsonArray.Where(v => v.IsString).Select(v => v.AsString).ToList()
            : null;

    private static DateTimeOffset? Instant(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsValidDateTime
            ? new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
            : null;

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"cycle_plans.failed: could not {operation} ({e.GetType().Name}).");

    private static PortError NoTransaction() =>
        new("cycle_plans.no_transaction: a plan can only be written inside a transaction, together with its audit entry.");
}
