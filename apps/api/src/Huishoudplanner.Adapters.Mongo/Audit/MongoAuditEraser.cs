using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Audit;

/// <summary>
/// <see cref="ForDeletingAuditEntries"/>: the only code that deletes audit entries. Each operation is one
/// <c>deleteMany</c>, which MongoDB applies atomically, so no transaction is used: there is no entity write and no audit entry
/// to keep consistent with it, and a transaction over a large log would only add a lifetime limit that can fail the delete.
/// </summary>
internal sealed class MongoAuditEraser : ForDeletingAuditEntries
{
    private readonly IMongoCollection<BsonDocument> auditLog;

    public MongoAuditEraser(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        auditLog = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.AuditLog);
    }

    public Task<OneOf<int, PortError>> ClearAsync(CancellationToken cancellationToken) =>
        DeleteAsync(new BsonDocument(), "clear", cancellationToken);

    public Task<OneOf<int, PortError>> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        DeleteAsync(new BsonDocument("at", new BsonDocument("$lt", new BsonDateTime(cutoff.UtcDateTime))), "delete the old entries of", cancellationToken);

    private async Task<OneOf<int, PortError>> DeleteAsync(BsonDocument filter, string operation, CancellationToken cancellationToken)
    {
        try
        {
            var result = await auditLog.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
            return (int)Math.Min(result.DeletedCount, int.MaxValue);
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            return new PortError($"audit.delete_failed: could not {operation} the audit log ({e.GetType().Name}).");
        }
    }
}
