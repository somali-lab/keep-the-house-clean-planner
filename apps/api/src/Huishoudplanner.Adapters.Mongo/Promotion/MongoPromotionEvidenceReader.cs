using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Promotion;

/// <summary>
/// <see cref="ForReadingPromotionEvidence"/> on the <c>occurrences</c> collection: the generated occurrences of one plan with a task, projected to
/// the four fields the promote rule needs. Read only; joins the running transaction when there is one.
/// </summary>
internal sealed class MongoPromotionEvidenceReader : ForReadingPromotionEvidence
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> occurrences;

    public MongoPromotionEvidenceReader(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        occurrences = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Occurrences);
    }

    public async Task<OneOf<IReadOnlyList<PromotionOccurrence>, PortError>> FindGeneratedOfPlanAsync(string planId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planId);
        if (!ObjectIdConverter.TryParse(planId, out _))
        {
            return OneOf<IReadOnlyList<PromotionOccurrence>, PortError>.FromT0([]);
        }

        var filter = new BsonDocument { { "planId", ObjectIdConverter.Parse(planId) }, { "origin", "generated" }, { "taskId", new BsonDocument("$type", "objectId") } };
        var projection = Builders<BsonDocument>.Projection.Include("taskId").Include("plannedDate").Include("date").Include("assigneeId");
        try
        {
            var query = MongoTransactionContext.Session is { } session ? occurrences.Find(session, filter) : occurrences.Find(filter);
            // Node sorts {date, taskNameSnapshot, _id}: when duplicate generated occurrences share a planned day the later one wins (byPlannedDay is last-wins).
            var rows = await query.Project(projection).Sort(Builders<BsonDocument>.Sort.Ascending("date").Ascending("taskNameSnapshot").Ascending("_id")).ToListAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<PromotionOccurrence>(rows.Count);
            foreach (var row in rows)
            {
                if (row["_id"].IsObjectId && row["taskId"].IsObjectId && row.TryGetValue("plannedDate", out var planned) && planned.IsValidDateTime &&
                    row.TryGetValue("date", out var date) && date.IsValidDateTime)
                {
                    result.Add(new PromotionOccurrence(
                        ObjectIdConverter.ToHex(row["_id"].AsObjectId),
                        ObjectIdConverter.ToHex(row["taskId"].AsObjectId),
                        new DateTimeOffset(planned.ToUniversalTime(), TimeSpan.Zero),
                        new DateTimeOffset(date.ToUniversalTime(), TimeSpan.Zero),
                        row.TryGetValue("assigneeId", out var assignee) && assignee.IsObjectId ? ObjectIdConverter.ToHex(assignee.AsObjectId) : null));
                }
            }

            return result;
        }
        catch (Exception e) when ((e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel)))
        {
            return new PortError($"promote.failed: could not read the promotion evidence ({e.GetType().Name}).");
        }
    }
}
