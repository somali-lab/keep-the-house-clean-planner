using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Rooms;

/// <summary>
/// <see cref="ForCheckingRoomUsage"/> by counting the documents of the existing <c>tasks</c> collection with that
/// <c>roomId</c> (active and inactive alike; the <c>roomId, active</c> index serves it). There is no task domain in this
/// application yet: only the count crosses the port.
/// </summary>
internal sealed class MongoRoomUsage : ForCheckingRoomUsage
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> tasks;

    public MongoRoomUsage(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        tasks = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Tasks);
    }

    public async Task<OneOf<int, PortError>> CountTasksInRoomAsync(string roomId, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(roomId, out var id))
        {
            return 0;
        }

        var filter = new BsonDocument("roomId", id);
        try
        {
            var count = MongoTransactionContext.Session is { } session
                ? await tasks.CountDocumentsAsync(session, filter, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await tasks.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (int)Math.Min(count, int.MaxValue);
        }
        catch (Exception e) when ((e is MongoException or TimeoutException) && !(e is MongoException m && m.HasErrorLabel(TransientLabel)))
        {
            return new PortError($"rooms.usage_failed: could not count the tasks of the room ({e.GetType().Name}).");
        }
    }
}
