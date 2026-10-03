using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Users;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo.Users;

/// <summary>
/// The <c>users</c> collection behind <see cref="ForStoringUsers"/>, and the narrow identity lookup
/// (<see cref="ForFindingUsers"/>) on the same documents. Documents keep the field names and BSON types of the Node server
/// (see <see cref="UserDocuments"/>); reads and writes enlist in the running transaction when there is one.
/// </summary>
/// <remarks>
/// Infrastructure failures (<see cref="MongoException"/>, <see cref="TimeoutException"/>) become a value-free
/// <see cref="PortError"/>, except a transient transaction error, which propagates so the runner retries the attempt.
/// </remarks>
internal sealed class MongoUserStore : ForStoringUsers, ForFindingUsers
{
    private const string TransientLabel = "TransientTransactionError";

    private readonly IMongoCollection<BsonDocument> users;

    public MongoUserStore(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        users = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Users);
    }

    public async Task<OneOf<UserPage, PortError>> ListAsync(UserQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filters = new List<FilterDefinition<BsonDocument>>();
        var filter = Builders<BsonDocument>.Filter;
        if (query.Active is { } active)
        {
            filters.Add(filter.Eq("active", active));
        }

        if (query.After is { } after)
        {
            var createdAt = after.CreatedAt.UtcDateTime;
            filters.Add(filter.Or(
                filter.Gt("createdAt", createdAt),
                filter.And(filter.Eq("createdAt", createdAt), filter.Gt("_id", ObjectId.Parse(after.Id)))));
        }

        try
        {
            var documents = await Query(filters.Count == 0 ? FilterDefinition<BsonDocument>.Empty : filter.And(filters))
                .Sort(Builders<BsonDocument>.Sort.Ascending("createdAt").Ascending("_id"))
                .Limit(query.Limit + 1)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var items = documents.Take(query.Limit).Select(UserDocuments.ToUser).ToList();
            var next = documents.Count > query.Limit ? UserCursor.For(items[^1]).Encode() : null;
            return new UserPage(items, next);
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return Failed("list", e);
        }
    }

    public async Task<OneOf<User, NotFound, PortError>> FindAsync(string userId, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(userId?.ToLowerInvariant(), out var id))
        {
            return new NotFound();
        }

        try
        {
            var document = await Query(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null ? new NotFound() : UserDocuments.ToUser(document);
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return Failed("find", e);
        }
    }

    /// <summary>The identity lookup: only role and active flag are read, with the same defaults as <see cref="UserDocuments"/>.</summary>
    async Task<OneOf<UserIdentity, NotFound, PortError>> ForFindingUsers.FindAsync(string userId, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(userId?.ToLowerInvariant(), out var id))
        {
            return new NotFound();
        }

        try
        {
            var document = await Query(Builders<BsonDocument>.Filter.Eq("_id", id))
                .Project(Builders<BsonDocument>.Projection.Include("role").Include("active"))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return document is null
                ? new NotFound()
                : new UserIdentity(ObjectIdConverter.ToHex(id), UserDocuments.RoleOf(document), UserDocuments.IsActive(document));
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return Failed("lookup", e);
        }
    }

    public async Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken)
    {
        try
        {
            var session = MongoTransactionContext.Session;
            return session is null
                ? await users.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await users.CountDocumentsAsync(session, FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return Failed("count", e);
        }
    }

    public async Task<OneOf<long, PortError>> CountOtherActiveAdminsAsync(string userId, CancellationToken cancellationToken)
    {
        if (!ObjectIdConverter.TryParse(userId?.ToLowerInvariant(), out var id))
        {
            return 0L;
        }

        var filter = Builders<BsonDocument>.Filter;
        // A document without a role is an administrator (installations from before roles), as in the Node server.
        var admins = filter.And(
            filter.Ne("_id", id),
            filter.Eq("active", true),
            filter.Or(filter.Eq("role", "admin"), filter.Exists("role", false)));
        try
        {
            var session = MongoTransactionContext.Session;
            return session is null
                ? await users.CountDocumentsAsync(admins, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await users.CountDocumentsAsync(session, admins, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return Failed("count admins", e);
        }
    }

    public async Task<OneOf<User, PortError>> InsertAsync(NewUser user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var id = ObjectId.GenerateNewId(now.UtcDateTime);
        var document = UserDocuments.ToDocument(id, user, now);
        try
        {
            var session = MongoTransactionContext.Session;
            if (session is null)
            {
                await users.InsertOneAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await users.InsertOneAsync(session, document, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return UserDocuments.ToUser(document);
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return Failed("insert", e);
        }
    }

    public async Task<OneOf<Success, NotFound, PortError>> UpdateAsync(
        string userId, UserPatch patch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!ObjectIdConverter.TryParse(userId?.ToLowerInvariant(), out var id))
        {
            return new NotFound();
        }

        var update = Builders<BsonDocument>.Update.Combine(UserDocuments.ToSets(patch, now));
        try
        {
            var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
            var session = MongoTransactionContext.Session;
            var result = session is null
                ? await users.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await users.UpdateOneAsync(session, filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.MatchedCount == 0 ? new NotFound() : new Success();
        }
        catch (Exception e) when (IsInfrastructureFailure(e) && !IsTransient(e))
        {
            return Failed("update", e);
        }
    }

    private IFindFluent<BsonDocument, BsonDocument> Query(FilterDefinition<BsonDocument> filter)
    {
        var session = MongoTransactionContext.Session;
        return session is null ? users.Find(filter) : users.Find(session, filter);
    }

    private static bool IsInfrastructureFailure(Exception e) => e is MongoException or TimeoutException;

    private static bool IsTransient(Exception e) => e is MongoException m && m.HasErrorLabel(TransientLabel);

    /// <summary>Value-free: the exception type only, never its text, which can echo a connection string.</summary>
    private static PortError Failed(string operation, Exception e) =>
        new($"users.{operation.Replace(' ', '_')}: the database failed ({e.GetType().Name}).");
}
