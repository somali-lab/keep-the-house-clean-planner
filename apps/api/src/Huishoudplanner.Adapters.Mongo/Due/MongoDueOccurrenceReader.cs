using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Due;

/// <summary>
/// <see cref="ForReadingDueOccurrences"/> on the <c>occurrences</c> collection: two grouped reads, each limited to the tasks asked for, that
/// return one row per task instead of the occurrences themselves. Read only; joins the running transaction when there is one.
/// </summary>
internal sealed class MongoDueOccurrenceReader : ForReadingDueOccurrences
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> occurrences;

    public MongoDueOccurrenceReader(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        occurrences = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Occurrences);
    }

    public async Task<OneOf<IReadOnlyDictionary<string, DateTimeOffset>, PortError>> FindFirstGeneratedPlannedDatesAsync(IReadOnlyCollection<string> taskIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        var ids = ToObjectIds(taskIds);
        if (ids.Count == 0)
        {
            return OneOf<IReadOnlyDictionary<string, DateTimeOffset>, PortError>.FromT0(new Dictionary<string, DateTimeOffset>());
        }

        BsonDocument[] pipeline =
        [
            new("$match", new BsonDocument { { "origin", "generated" }, { "taskId", new BsonDocument("$in", ids) } }),
            new("$group", new BsonDocument { { "_id", "$taskId" }, { "plannedDate", new BsonDocument("$min", "$plannedDate") } }),
        ];
        try
        {
            var rows = await AggregateAsync(pipeline, cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row["_id"].IsObjectId && row.TryGetValue("plannedDate", out var planned) && planned.IsValidDateTime)
                {
                    result[ObjectIdConverter.ToHex(row["_id"].AsObjectId)] = new DateTimeOffset(planned.ToUniversalTime(), TimeSpan.Zero);
                }
            }

            return OneOf<IReadOnlyDictionary<string, DateTimeOffset>, PortError>.FromT0(result);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the first planned dates", e);
        }
    }

    public async Task<OneOf<IReadOnlyDictionary<string, UpcomingOccurrence>, PortError>> FindNextOpenAsync(DateTimeOffset from, IReadOnlyCollection<string> taskIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        var ids = ToObjectIds(taskIds);
        if (ids.Count == 0)
        {
            return OneOf<IReadOnlyDictionary<string, UpcomingOccurrence>, PortError>.FromT0(new Dictionary<string, UpcomingOccurrence>());
        }

        BsonDocument[] pipeline =
        [
            new("$match", new BsonDocument
            {
                { "status", "open" },
                { "date", new BsonDocument("$gte", new BsonDateTime(from.UtcDateTime)) },
                { "taskId", new BsonDocument("$in", ids) },
            }),
            new("$sort", new BsonDocument { { "date", 1 }, { "taskNameSnapshot", 1 }, { "_id", 1 } }),
            new("$group", new BsonDocument
            {
                { "_id", "$taskId" },
                { "occurrenceId", new BsonDocument("$first", "$_id") },
                { "date", new BsonDocument("$first", "$date") },
                { "assigneeId", new BsonDocument("$first", "$assigneeId") },
            }),
        ];
        try
        {
            var rows = await AggregateAsync(pipeline, cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<string, UpcomingOccurrence>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row["_id"].IsObjectId && row["occurrenceId"].IsObjectId && row["date"].IsValidDateTime)
                {
                    var assignee = row.TryGetValue("assigneeId", out var value) && value.IsObjectId ? ObjectIdConverter.ToHex(value.AsObjectId) : null;
                    result[ObjectIdConverter.ToHex(row["_id"].AsObjectId)] = new UpcomingOccurrence(
                        ObjectIdConverter.ToHex(row["occurrenceId"].AsObjectId),
                        new DateTimeOffset(row["date"].ToUniversalTime(), TimeSpan.Zero),
                        assignee);
                }
            }

            return OneOf<IReadOnlyDictionary<string, UpcomingOccurrence>, PortError>.FromT0(result);
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the next open occurrences", e);
        }
    }

    private static BsonArray ToObjectIds(IReadOnlyCollection<string> ids) =>
        new(ids.Where(id => ObjectIdConverter.TryParse(id, out _)).Select(id => (BsonValue)ObjectIdConverter.Parse(id)));

    private Task<List<BsonDocument>> AggregateAsync(BsonDocument[] pipeline, CancellationToken cancellationToken)
    {
        var definition = PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline);
        var cursor = MongoTransactionContext.Session is { } session
            ? occurrences.Aggregate(session, definition, cancellationToken: cancellationToken)
            : occurrences.Aggregate(definition, cancellationToken: cancellationToken);
        return cursor.ToListAsync(cancellationToken);
    }

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"due.failed: could not {operation} ({e.GetType().Name}).");
}
