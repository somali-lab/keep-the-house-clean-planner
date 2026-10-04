using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Badges;

/// <summary>
/// <see cref="ForReadingBadgeEvidence"/> on the <c>occurrences</c> and <c>pointEntries</c> collections of the Node server (ADR-0014). The executions are
/// read from the occurrences, not from the ledger, so work of 0 points counts; the credit rule is the one of the ledger (<c>completedBy</c>, else the
/// assignee). For a few people the filter matches on the indexed fields (<c>completedBy</c> or <c>assigneeId</c> with <c>status</c>), so a check-off does
/// not scan every done occurrence. Reads only; they join the running transaction when there is one.
/// </summary>
internal sealed class MongoBadgeEvidenceStore : ForReadingBadgeEvidence
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> occurrences;
    private readonly IMongoCollection<BsonDocument> entries;

    public MongoBadgeEvidenceStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        var database = MongoClientFactory.GetDatabase(client, options);
        occurrences = database.GetCollection<BsonDocument>(MongoCollections.Occurrences);
        entries = database.GetCollection<BsonDocument>(MongoCollections.PointEntries);
    }

    public async Task<OneOf<IReadOnlyList<CreditedExecution>, PortError>> FindCreditedExecutionsAsync(IReadOnlyCollection<string>? personIds, CancellationToken cancellationToken)
    {
        var filter = new BsonDocument("status", "done");
        HashSet<ObjectId>? wanted = null;
        if (personIds is not null)
        {
            wanted = [.. personIds.Where(id => ObjectIdConverter.TryParse(id, out _)).Select(ObjectIdConverter.Parse)];
            if (wanted.Count == 0)
            {
                return OneOf<IReadOnlyList<CreditedExecution>, PortError>.FromT0([]);
            }

            var ids = new BsonArray(wanted);
            filter.Add("$or", new BsonArray
            {
                new BsonDocument("completedBy", new BsonDocument("$in", ids)),
                new BsonDocument { { "completedBy", BsonNull.Value }, { "assigneeId", new BsonDocument("$in", ids) } },
            });
        }

        try
        {
            var find = MongoTransactionContext.Session is { } session ? occurrences.Find(session, filter) : occurrences.Find(filter);
            var documents = await find
                .Project(new BsonDocument { { "taskId", 1 }, { "durationMinutesSnapshot", 1 }, { "completedAt", 1 }, { "date", 1 }, { "completedBy", 1 }, { "assigneeId", 1 } })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var result = new List<CreditedExecution>(documents.Count);
            foreach (var document in documents)
            {
                if (Credit(document, wanted) is { } credited)
                {
                    result.Add(credited);
                }
            }

            return result;
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the executions", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<OnTimeWeek>, PortError>> FindOnTimeWeeksAsync(IReadOnlyCollection<string>? personIds, CancellationToken cancellationToken)
    {
        var filter = new BsonDocument("kind", PointNames.ToWire(PointEntryKind.BonusWeekOnTime));
        if (personIds is not null)
        {
            var ids = personIds.Where(id => ObjectIdConverter.TryParse(id, out _)).Select(ObjectIdConverter.Parse).ToList();
            if (ids.Count == 0)
            {
                return OneOf<IReadOnlyList<OnTimeWeek>, PortError>.FromT0([]);
            }

            filter.Add("personId", new BsonDocument("$in", new BsonArray(ids)));
        }

        try
        {
            var find = MongoTransactionContext.Session is { } session ? entries.Find(session, filter) : entries.Find(filter);
            var documents = await find
                .Project(new BsonDocument { { "personId", 1 }, { "date", 1 } })
                .Sort(new BsonDocument { { "date", 1 }, { "_id", 1 } })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var result = new List<OnTimeWeek>(documents.Count);
            foreach (var document in documents)
            {
                if (document.TryGetValue("personId", out var person) && person.IsObjectId && document.TryGetValue("date", out var date) && date.IsValidDateTime)
                {
                    result.Add(new OnTimeWeek(ObjectIdConverter.ToHex(person.AsObjectId), new DateTimeOffset(date.ToUniversalTime(), TimeSpan.Zero)));
                }
            }

            return result;
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the on-time weeks", e);
        }
    }

    /// <summary>
    /// The execution of one done occurrence and the person it is credited to; <see langword="null"/> when nobody can be credited, the person is not one
    /// of the wanted ones, or the occurrence has no readable moment.
    /// </summary>
    private static CreditedExecution? Credit(BsonDocument document, HashSet<ObjectId>? wanted)
    {
        var person = document.TryGetValue("completedBy", out var by) && by.IsObjectId
            ? by.AsObjectId
            : document.TryGetValue("assigneeId", out var assignee) && assignee.IsObjectId && (!document.TryGetValue("completedBy", out var none) || none.IsBsonNull)
                ? assignee.AsObjectId
                : (ObjectId?)null;
        if (person is not { } credited || (wanted is not null && !wanted.Contains(credited)))
        {
            return null;
        }

        var at = document.TryGetValue("completedAt", out var completed) && completed.IsValidDateTime
            ? completed
            : document.TryGetValue("date", out var date) && date.IsValidDateTime ? date : null;
        if (at is null)
        {
            return null;
        }

        return new CreditedExecution(
            ObjectIdConverter.ToHex(credited),
            new BadgeExecution(
                ObjectIdConverter.ToHex(document["_id"].AsObjectId),
                document.TryGetValue("taskId", out var task) && task.IsObjectId ? ObjectIdConverter.ToHex(task.AsObjectId) : null,
                document.TryGetValue("durationMinutesSnapshot", out var minutes) && minutes.IsNumeric ? minutes.ToInt32() : 0,
                new DateTimeOffset(at.ToUniversalTime(), TimeSpan.Zero)));
    }

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"badgeEvidence.failed: could not {operation} ({e.GetType().Name}).");
}
