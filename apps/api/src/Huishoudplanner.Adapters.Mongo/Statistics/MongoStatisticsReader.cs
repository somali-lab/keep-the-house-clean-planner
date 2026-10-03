using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Statistics;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Statistics;

/// <summary>
/// <see cref="ForReadingStatistics"/> on the <c>occurrences</c>, <c>users</c>, <c>tasks</c> and <c>rooms</c> collections, which the Node server
/// shares. Only the fields the reports read are projected; documents are mapped by hand so the driver types stay in this class. Reads join the
/// running transaction when there is one.
/// </summary>
internal sealed class MongoStatisticsReader : ForReadingStatistics
{
    private const string TransientLabel = "TransientTransactionError";

    private static readonly ProjectionDefinition<BsonDocument> OccurrenceFields = Builders<BsonDocument>.Projection
        .Include("cycleId").Include("taskId").Include("date").Include("plannedDate").Include("assigneeId").Include("status")
        .Include("completedAt").Include("completedBy").Include("durationMinutesSnapshot").Include("taskNameSnapshot")
        .Include("roomIdSnapshot").Include("recordedDone");

    private readonly IMongoCollection<BsonDocument> occurrences;
    private readonly IMongoCollection<BsonDocument> users;
    private readonly IMongoCollection<BsonDocument> tasks;
    private readonly IMongoCollection<BsonDocument> rooms;

    public MongoStatisticsReader(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        var database = MongoClientFactory.GetDatabase(client, options);
        occurrences = database.GetCollection<BsonDocument>(MongoCollections.Occurrences);
        users = database.GetCollection<BsonDocument>(MongoCollections.Users);
        tasks = database.GetCollection<BsonDocument>(MongoCollections.Tasks);
        rooms = database.GetCollection<BsonDocument>(MongoCollections.Rooms);
    }

    public async Task<OneOf<IReadOnlyList<StatisticsOccurrence>, PortError>> FindOccurrencesAsync(
        IReadOnlyCollection<string> cycleIds,
        DateTimeOffset? rangeStart,
        DateTimeOffset? rangeEnd,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycleIds);
        var ids = cycleIds.Where(id => ObjectIdConverter.TryParse(id, out _)).Select(ObjectIdConverter.Parse).ToList();
        if (ids.Count == 0)
        {
            return OneOf<IReadOnlyList<StatisticsOccurrence>, PortError>.FromT0([]);
        }

        var filter = new BsonDocument("cycleId", new BsonDocument("$in", new BsonArray(ids)));
        if (rangeStart is { } start && rangeEnd is { } end)
        {
            filter["date"] = new BsonDocument { { "$gte", new BsonDateTime(start.UtcDateTime) }, { "$lt", new BsonDateTime(end.UtcDateTime) } };
        }

        try
        {
            var documents = await Find(occurrences, filter).Project(OccurrenceFields).ToListAsync(cancellationToken).ConfigureAwait(false);
            return OneOf<IReadOnlyList<StatisticsOccurrence>, PortError>.FromT0(documents.ConvertAll(ToOccurrence));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the occurrences", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<StatisticsPerson>, PortError>> ListPeopleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var documents = await Find(users, new BsonDocument())
                .Project(Builders<BsonDocument>.Projection.Include("name").Include("active"))
                .Sort(Builders<BsonDocument>.Sort.Ascending("createdAt").Ascending("_id"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return OneOf<IReadOnlyList<StatisticsPerson>, PortError>.FromT0(
                documents.ConvertAll(d => new StatisticsPerson(Hex(d), Text(d, "name") ?? string.Empty, Flag(d, "active", true))));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the people", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<StatisticsTask>, PortError>> ListTasksAsync(CancellationToken cancellationToken)
    {
        try
        {
            var documents = await Find(tasks, new BsonDocument())
                .Project(Builders<BsonDocument>.Projection.Include("name").Include("roomId").Include("intervalKey").Include("active"))
                .Sort(Builders<BsonDocument>.Sort.Ascending("name").Ascending("_id"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return OneOf<IReadOnlyList<StatisticsTask>, PortError>.FromT0(
                documents.ConvertAll(d => new StatisticsTask(Hex(d), Text(d, "name") ?? string.Empty, Id(d, "roomId") ?? string.Empty, Text(d, "intervalKey") ?? string.Empty, Flag(d, "active", true))));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the tasks", e);
        }
    }

    public async Task<OneOf<IReadOnlyList<StatisticsRoom>, PortError>> ListRoomsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var documents = await Find(rooms, new BsonDocument())
                .Project(Builders<BsonDocument>.Projection.Include("name"))
                .Sort(Builders<BsonDocument>.Sort.Ascending("sortOrder").Ascending("name"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return OneOf<IReadOnlyList<StatisticsRoom>, PortError>.FromT0(documents.ConvertAll(d => new StatisticsRoom(Hex(d), Text(d, "name") ?? string.Empty)));
        }
        catch (Exception e) when (IsFailure(e))
        {
            return Failed("read the rooms", e);
        }
    }

    private static IFindFluent<BsonDocument, BsonDocument> Find(IMongoCollection<BsonDocument> collection, BsonDocument filter) =>
        MongoTransactionContext.Session is { } session ? collection.Find(session, filter) : collection.Find(filter);

    private static StatisticsOccurrence ToOccurrence(BsonDocument document) => new(
        Id(document, "cycleId") ?? string.Empty,
        Id(document, "taskId"),
        Instant(document, "date") ?? DateTimeOffset.UnixEpoch,
        Instant(document, "plannedDate"),
        Id(document, "assigneeId"),
        document.TryGetValue("status", out var status) && status.IsString && OccurrenceNames.TryParseStatus(status.AsString, out var parsed) ? parsed : OccurrenceStatus.Open,
        Instant(document, "completedAt"),
        Id(document, "completedBy"),
        document.TryGetValue("durationMinutesSnapshot", out var duration) && duration.IsNumeric ? duration.ToInt32() : 0,
        Text(document, "taskNameSnapshot") ?? string.Empty,
        Id(document, "roomIdSnapshot"),
        Flag(document, "recordedDone", false));

    private static string Hex(BsonDocument document) => ObjectIdConverter.ToHex(document["_id"].AsObjectId);

    private static string? Id(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsObjectId ? ObjectIdConverter.ToHex(value.AsObjectId) : null;

    private static string? Text(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsString ? value.AsString : null;

    private static bool Flag(BsonDocument document, string field, bool fallback) =>
        document.TryGetValue(field, out var value) && value.IsBoolean ? value.AsBoolean : fallback;

    private static DateTimeOffset? Instant(BsonDocument document, string field) =>
        document.TryGetValue(field, out var value) && value.IsValidDateTime
            ? new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
            : null;

    private static bool IsFailure(Exception e) =>
        (e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel));

    private static PortError Failed(string operation, Exception e) =>
        new($"statistics.failed: could not {operation} ({e.GetType().Name}).");
}
