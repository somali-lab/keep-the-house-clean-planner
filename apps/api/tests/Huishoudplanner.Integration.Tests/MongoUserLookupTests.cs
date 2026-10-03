using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Domain.Identity;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>The Mongo implementation of <c>ForFindingUsers</c>: role defaults of legacy documents, active flag, id parsing.</summary>
public sealed class MongoUserLookupTests(MongoContainerFixture mongo) : IAsyncLifetime
{
    private readonly MongoOptions options = new()
    {
        ConnectionString = mongo.ConnectionString,
        DatabaseName = MongoContainerFixture.NewDatabaseName(),
    };

    private IMongoClient client = null!;
    private IMongoCollection<BsonDocument> users = null!;
    private MongoUserLookup lookup = null!;

    public ValueTask InitializeAsync()
    {
        client = MongoClientFactory.CreateClient(options);
        users = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.Users);
        lookup = new MongoUserLookup(client, options);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await client.DropDatabaseAsync(options.DatabaseName, TestContext.Current.CancellationToken);
        client.Dispose();
    }

    private async Task<ObjectId> Insert(BsonDocument document)
    {
        var id = ObjectId.GenerateNewId();
        document["_id"] = id;
        await users.InsertOneAsync(document, cancellationToken: TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<UserIdentity> Found(string id) =>
        (await lookup.FindAsync(id, TestContext.Current.CancellationToken)).AsT0;

    [Theory]
    [InlineData("admin", Role.Admin)]
    [InlineData("planner", Role.Planner)]
    [InlineData("member", Role.Member)]
    public async Task Find_activeUser_returnsItsRole(string role, Role expected)
    {
        var id = await Insert(new BsonDocument { { "name", "A" }, { "active", true }, { "role", role } });

        var user = await Found(id.ToString());

        user.Should().Be(new UserIdentity(id.ToString(), expected, true));
    }

    [Fact]
    public async Task Find_documentWithoutRole_isAdmin_likeInstallationsThatPredateRoles()
    {
        var id = await Insert(new BsonDocument { { "active", true } });

        (await Found(id.ToString())).Role.Should().Be(Role.Admin);
    }

    [Fact]
    public async Task Find_unrecognisedRole_getsTheLeastPrivilege()
    {
        var id = await Insert(new BsonDocument { { "active", true }, { "role", "superuser" } });

        (await Found(id.ToString())).Role.Should().Be(Role.Member);
    }

    [Fact]
    public async Task Find_inactiveUser_isReportedInactive()
    {
        var id = await Insert(new BsonDocument { { "active", false }, { "role", "admin" } });

        (await Found(id.ToString())).Active.Should().BeFalse();
    }

    [Fact]
    public async Task Find_documentWithoutActiveFlag_isInactive()
    {
        var id = await Insert(new BsonDocument { { "role", "admin" } });

        (await Found(id.ToString())).Active.Should().BeFalse();
    }

    [Fact]
    public async Task Find_uppercaseHexId_findsTheUser_andReturnsTheCanonicalId()
    {
        var id = await Insert(new BsonDocument { { "active", true }, { "role", "member" } });

        (await Found(id.ToString().ToUpperInvariant())).Id.Should().Be(id.ToString());
    }

    [Theory]
    [InlineData("0123456789abcdef01234567")]
    [InlineData("nope")]
    [InlineData("")]
    public async Task Find_unknownOrMalformedId_isNotFound(string id)
    {
        var result = await lookup.FindAsync(id, TestContext.Current.CancellationToken);

        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Find_withUnreachableDatabase_isAPortError_withoutTheConnectionString()
    {
        var unreachable = new MongoOptions { ConnectionString = "mongodb://127.0.0.1:1/x?serverSelectionTimeoutMS=300", DatabaseName = "x" };
        var settings = MongoClientSettings.FromConnectionString(unreachable.ConnectionString);
        using var deadClient = new MongoClient(settings);

        var result = await new MongoUserLookup(deadClient, unreachable).FindAsync("0123456789abcdef01234567", TestContext.Current.CancellationToken);

        result.AsT2.Message.Should().NotContain("127.0.0.1");
    }
}
