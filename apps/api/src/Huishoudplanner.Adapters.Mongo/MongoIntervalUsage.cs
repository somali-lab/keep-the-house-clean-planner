using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// <see cref="ForCheckingIntervalUsage"/>: the distinct <c>intervalKey</c> of the <c>tasks</c> collection (<c>intervalKeysInUse</c> of the
/// Node server). Interim read until the task store of slice 2.1; it joins the running transaction when there is one.
/// </summary>
internal sealed class MongoIntervalUsage : ForCheckingIntervalUsage
{
    private readonly IMongoCollection<BsonDocument> tasks;

    public MongoIntervalUsage(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        tasks = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Tasks);
    }

    public async Task<OneOf<IReadOnlyList<string>, PortError>> GetKeysInUseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filter = FilterDefinition<BsonDocument>.Empty;
            var cursor = MongoTransactionContext.Session is { } session
                ? await tasks.DistinctAsync<string>(session, "intervalKey", filter, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await tasks.DistinctAsync<string>("intervalKey", filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            return await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return new PortError($"tasks.failed: could not read the intervals in use ({e.GetType().Name}).");
        }
    }

    private static bool IsInfrastructureFailure(Exception e) => e is MongoException or TimeoutException;

    // A transient transaction error is not a value: it propagates so the runner retries the whole attempt.
    private static bool IsTransient(Exception e) => e is MongoException m && m.HasErrorLabel("TransientTransactionError");
}
