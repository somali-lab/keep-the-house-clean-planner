using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Adapters.Mongo;

/// <summary>
/// Looks a user up for identity: only role and active flag are read. Interim until the users repository of slice 1.1.
/// Mirrors <c>findUserById</c> of the Node server: a document without a role is an admin (installations that predate roles).
/// A role this code does not know gets the least privilege instead of failing.
/// </summary>
public sealed class MongoUserLookup : ForFindingUsers
{
    private readonly IMongoCollection<BsonDocument> users;

    public MongoUserLookup(IMongoClient client, MongoOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        users = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Users);
    }

    public async Task<OneOf<UserIdentity, NotFound, PortError>> FindAsync(string userId, CancellationToken cancellationToken)
    {
        if (!ObjectId.TryParse(userId, out var id))
        {
            return new NotFound();
        }

        try
        {
            var document = await users
                .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
                .Project(Builders<BsonDocument>.Projection.Include("role").Include("active"))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return document is null ? new NotFound() : new UserIdentity(id.ToString(), RoleOf(document), IsActive(document));
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return new PortError($"The user lookup failed: {ex.GetType().Name}");
        }
    }

    private static bool IsActive(BsonDocument document) =>
        document.TryGetValue("active", out var active) && active.IsBoolean && active.AsBoolean;

    private static Role RoleOf(BsonDocument document)
    {
        if (!document.TryGetValue("role", out var role) || role.IsBsonNull)
        {
            return Role.Admin;
        }

        return role.IsString ? role.AsString switch
        {
            "admin" => Role.Admin,
            "planner" => Role.Planner,
            _ => Role.Member,
        }
        : Role.Member;
    }
}
