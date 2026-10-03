using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>Reachability through a <c>ping</c> command, bounded so a dead server answers within seconds.</summary>
public sealed class MongoHealthCheck : ForCheckingHealth
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly IMongoDatabase database;
    private readonly TimeSpan timeout;

    public MongoHealthCheck(IMongoClient client, MongoOptions options, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        database = MongoClientFactory.GetDatabase(client, options);
        this.timeout = timeout ?? DefaultTimeout;
    }

    public async Task<OneOf<Success, PortError>> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: bounded.Token).ConfigureAwait(false);
            return new Success();
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new PortError($"MongoDB is unavailable: {ex.GetType().Name}");
        }
    }
}
