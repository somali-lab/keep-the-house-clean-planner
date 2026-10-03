using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Audit;

/// <summary>
/// <see cref="ForReadingOccurrenceContext"/> by reading the snapshots (<c>taskNameSnapshot</c>, <c>roomNameSnapshot</c>,
/// <c>date</c>) of the existing <c>occurrences</c> collection. There is no occurrence domain in this application yet: only the
/// context crosses the port.
/// </summary>
internal sealed class MongoOccurrenceContext : ForReadingOccurrenceContext
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> occurrences;

    public MongoOccurrenceContext(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        occurrences = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Occurrences);
    }

    public async Task<OneOf<IReadOnlyDictionary<string, OccurrenceContext>, PortError>> FindAsync(IReadOnlyCollection<string> occurrenceIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrenceIds);
        var ids = new BsonArray();
        foreach (var id in occurrenceIds)
        {
            if (ObjectIdConverter.TryParse(id, out var objectId))
            {
                ids.Add(objectId);
            }
        }

        var found = new Dictionary<string, OccurrenceContext>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return found;
        }

        var projection = Builders<BsonDocument>.Projection.Include("taskNameSnapshot").Include("roomNameSnapshot").Include("date");
        var filter = new BsonDocument("_id", new BsonDocument("$in", ids));
        try
        {
            var find = MongoTransactionContext.Session is { } session ? occurrences.Find(session, filter) : occurrences.Find(filter);
            var documents = await find.Project(projection).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var document in documents)
            {
                found[document["_id"].AsObjectId.ToString()] = new OccurrenceContext(
                    document.TryGetValue("taskNameSnapshot", out var task) && task.IsString ? task.AsString : string.Empty,
                    document.TryGetValue("roomNameSnapshot", out var room) && room.IsString ? room.AsString : null,
                    document.TryGetValue("date", out var date) && date.IsValidDateTime ? new DateTimeOffset(date.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.UnixEpoch);
            }

            return found;
        }
        catch (Exception e) when ((e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel)))
        {
            return new PortError($"audit.occurrence_context_failed: could not read the occurrences ({e.GetType().Name}).");
        }
    }
}
