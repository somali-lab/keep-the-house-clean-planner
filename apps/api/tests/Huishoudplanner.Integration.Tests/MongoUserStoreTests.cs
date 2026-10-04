using Huishoudplanner.Adapters.Mongo;
using Huishoudplanner.Adapters.Mongo.Users;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Users;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// The Mongo implementation of <c>ForStoringUsers</c> and of the identity lookup <c>ForFindingUsers</c> (which replaced the
/// interim <c>MongoUserLookup</c>; its cases are ported here): defaults of legacy documents, the active flag, id parsing,
/// paging, the administrator count, partial updates and taking part in a transaction.
/// </summary>
public sealed class MongoUserStoreTests(MongoContainerFixture mongo) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    private readonly MongoOptions options = new()
    {
        ConnectionString = mongo.ConnectionString,
        DatabaseName = MongoContainerFixture.NewDatabaseName(),
    };

    private IMongoClient client = null!;
    private IMongoCollection<BsonDocument> users = null!;
    private MongoUserStore store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public async ValueTask InitializeAsync()
    {
        client = MongoClientFactory.CreateClient(options);
        var database = MongoClientFactory.GetDatabase(client, options);
        await database.CreateCollectionAsync(MongoCollections.AuditLog, cancellationToken: Ct);
        users = database.GetCollection<BsonDocument>(MongoCollections.Users);
        store = new MongoUserStore(client, options);
    }

    public async ValueTask DisposeAsync()
    {
        await client.DropDatabaseAsync(options.DatabaseName, Ct);
        client.Dispose();
    }

    private async Task<ObjectId> Insert(BsonDocument document)
    {
        var id = ObjectId.GenerateNewId();
        document["_id"] = id;
        await users.InsertOneAsync(document, cancellationToken: Ct);
        return id;
    }

    private static NewUser Anna(string name = "Anna", Role role = Role.Member) =>
        new(name, "#16a34a", role, [1, 3], new DailyMinutes(45, 90), new DailyMinutes(60, 120));

    private async Task<UserIdentity> Identity(string id) => (await ((ForFindingUsers)store).FindAsync(id, Ct)).AsT0;

    // ---- identity lookup (ported from MongoUserLookupTests) ---------------------------------

    [Theory]
    [InlineData("admin", Role.Admin)]
    [InlineData("planner", Role.Planner)]
    [InlineData("member", Role.Member)]
    public async Task Identity_activeUser_returnsItsRole(string role, Role expected)
    {
        var id = await Insert(new BsonDocument { { "name", "A" }, { "active", true }, { "role", role } });

        (await Identity(id.ToString())).Should().Be(new UserIdentity(id.ToString(), expected, true));
    }

    [Fact]
    public async Task Identity_documentWithoutRole_isAdmin_likeInstallationsThatPredateRoles()
    {
        var id = await Insert(new BsonDocument { { "active", true } });

        (await Identity(id.ToString())).Role.Should().Be(Role.Admin);
    }

    [Fact]
    public async Task Identity_unrecognisedRole_getsTheLeastPrivilege()
    {
        var id = await Insert(new BsonDocument { { "active", true }, { "role", "superuser" } });

        (await Identity(id.ToString())).Role.Should().Be(Role.Member);
    }

    [Fact]
    public async Task Identity_inactiveUser_isReportedInactive()
    {
        var id = await Insert(new BsonDocument { { "active", false }, { "role", "admin" } });

        (await Identity(id.ToString())).Active.Should().BeFalse();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("string")]
    public async Task Identity_documentWithoutABooleanActiveFlag_isInactive(string kind)
    {
        var document = new BsonDocument { { "role", "admin" } };
        if (kind == "string")
        {
            document["active"] = "true";
        }

        var id = await Insert(document);

        (await Identity(id.ToString())).Active.Should().BeFalse();
    }

    [Fact]
    public async Task Identity_uppercaseHexId_findsTheUser_andReturnsTheCanonicalId()
    {
        var id = await Insert(new BsonDocument { { "active", true }, { "role", "member" } });

        (await Identity(id.ToString().ToUpperInvariant())).Id.Should().Be(id.ToString());
    }

    [Theory]
    [InlineData("0123456789abcdef01234567")]
    [InlineData("nope")]
    [InlineData("")]
    public async Task Identity_unknownOrMalformedId_isNotFound(string id)
    {
        var result = await ((ForFindingUsers)store).FindAsync(id, Ct);

        result.IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Identity_withUnreachableDatabase_isAPortError_withoutTheConnectionString()
    {
        var unreachable = new MongoOptions { ConnectionString = "mongodb://127.0.0.1:1/x?serverSelectionTimeoutMS=300", DatabaseName = "x" };
        using var deadClient = new MongoClient(MongoClientSettings.FromConnectionString(unreachable.ConnectionString));

        var result = await ((ForFindingUsers)new MongoUserStore(deadClient, unreachable)).FindAsync("0123456789abcdef01234567", Ct);

        result.AsT2.Message.Should().NotContain("127.0.0.1");
    }

    // ---- insert and find --------------------------------------------------------------------

    [Fact]
    public async Task Insert_storesTheNodeDocumentShape_andReturnsTheUser()
    {
        var user = (await store.InsertAsync(Anna(), Now, Ct)).AsT0;

        var stored = await users.Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(user.Id))).FirstAsync(Ct);
        stored.Names.Should().Equal("_id", "name", "color", "active", "role", "unavailableWeekdays", "dailyBudgetMinutes", "maxDailyMinutes", "browserNotifications", "createdAt", "updatedAt", "version");
        stored["unavailableWeekdays"].AsBsonArray.Select(d => d.BsonType).Should().OnlyContain(t => t == BsonType.Int32);
        stored["browserNotifications"].ToJson().Should().Be(BsonDocument.Parse("{ enabled: false, times: [] }").ToJson());
        stored["createdAt"].ToUniversalTime().Should().Be(Now.UtcDateTime);
        user.Should().BeEquivalentTo(new { Name = "Anna", Active = true, Role = Role.Member, CreatedAt = Now, UpdatedAt = Now });
    }

    [Fact]
    public async Task Find_returnsTheStoredUser_unknownIsNotFound()
    {
        var created = (await store.InsertAsync(Anna(), Now, Ct)).AsT0;

        var found = (await store.FindAsync(created.Id, Ct)).AsT0;

        found.Should().BeEquivalentTo(created);
        (await store.FindAsync("0123456789abcdef01234567", Ct)).IsT1.Should().BeTrue();
        (await store.FindAsync("nope", Ct)).IsT1.Should().BeTrue();
    }

    [Fact]
    public async Task Find_documentWithOddValues_readsWithDefaults()
    {
        var id = await Insert(new BsonDocument
        {
            { "name", "Oud" },
            { "active", true },
            { "unavailableWeekdays", new BsonArray { 2.0, 4 } },
            { "dailyBudgetMinutes", new BsonDocument { { "weekday", 60.0 }, { "weekend", 120 } } },
        });

        var user = (await store.FindAsync(id.ToString(), Ct)).AsT0;

        user.Role.Should().Be(Role.Admin);
        user.UnavailableWeekdays.Should().Equal(2, 4);
        user.DailyBudgetMinutes.Should().Be(new DailyMinutes(60, 120));
        user.MaxDailyMinutes.Should().Be(new DailyMinutes(480, 480));
        user.BrowserNotifications.Should().Be(BrowserNotifications.Disabled);
    }

    // ---- list -------------------------------------------------------------------------------

    [Fact]
    public async Task List_ordersByCreationThenId_andPagesWithoutGapsOrRepeats()
    {
        for (var i = 0; i < 5; i++)
        {
            await store.InsertAsync(Anna($"U{i}"), Now.AddMinutes(i / 2), Ct);
        }

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var after = cursor is null ? null : (UserCursor.TryParse(cursor, out var parsed) ? parsed : null);
            var page = (await store.ListAsync(new UserQuery(null, after, 2), Ct)).AsT0;
            seen.AddRange(page.Items.Select(u => u.Name));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.Should().Equal("U0", "U1", "U2", "U3", "U4");
    }

    [Fact]
    public async Task List_filtersByActive()
    {
        await store.InsertAsync(Anna("Actief"), Now, Ct);
        await Insert(new BsonDocument { { "name", "Uit" }, { "active", false }, { "createdAt", new BsonDateTime(Now.UtcDateTime) } });

        (await store.ListAsync(new UserQuery(true, null, 10), Ct)).AsT0.Items.Select(u => u.Name).Should().Equal("Actief");
        (await store.ListAsync(new UserQuery(false, null, 10), Ct)).AsT0.Items.Select(u => u.Name).Should().Equal("Uit");
    }

    // ---- counts -----------------------------------------------------------------------------

    [Fact]
    public async Task CountOtherActiveAdmins_countsActiveAdminsAndRolelessDocuments_exceptTheGivenUser()
    {
        var self = (await store.InsertAsync(Anna("Zelf", Role.Admin), Now, Ct)).AsT0;
        await store.InsertAsync(Anna("Admin", Role.Admin), Now, Ct);
        await store.InsertAsync(Anna("Planner", Role.Planner), Now, Ct);
        await Insert(new BsonDocument { { "name", "Roleless" }, { "active", true } });
        await Insert(new BsonDocument { { "name", "Inactive admin" }, { "active", false }, { "role", "admin" } });

        (await store.CountOtherActiveAdminsAsync(self.Id, Ct)).AsT0.Should().Be(2);
        (await store.CountAsync(Ct)).AsT0.Should().Be(5);
    }

    // ---- update -----------------------------------------------------------------------------

    [Fact]
    public async Task Update_setsOnlyThePatchFields_andUpdatedAt_keepingOtherFields()
    {
        var user = (await store.InsertAsync(Anna(), Now, Ct)).AsT0;
        await users.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(user.Id)),
            Builders<BsonDocument>.Update.Set("nodeOnlyField", 1),
            cancellationToken: Ct);
        var later = Now.AddHours(1);

        var result = await store.UpdateAsync(user.Id, new UserPatch(Name: "Bea", UnavailableWeekdays: [0]), later, Ct);

        result.IsT0.Should().BeTrue();
        var stored = await users.Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(user.Id))).FirstAsync(Ct);
        stored["name"].AsString.Should().Be("Bea");
        stored["unavailableWeekdays"].AsBsonArray.Select(d => d.AsInt32).Should().Equal(0);
        stored["color"].AsString.Should().Be("#16a34a");
        stored["nodeOnlyField"].AsInt32.Should().Be(1);
        stored["updatedAt"].ToUniversalTime().Should().Be(later.UtcDateTime);
        stored["createdAt"].ToUniversalTime().Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task Update_unknownUser_isNotFound()
    {
        var result = await store.UpdateAsync("0123456789abcdef01234567", new UserPatch(Name: "X"), Now, Ct);

        result.IsT1.Should().BeTrue();
    }

    // ---- transactions -----------------------------------------------------------------------

    [Fact]
    public async Task InsertAndAudit_inOneTransaction_commitTogether_andRollBackTogether()
    {
        var time = new FixedTimeProvider(Now);
        var runner = new MongoTransactionRunner(client, time);
        var recorder = new MongoAuditRecorder(client, options, time);
        var audit = MongoClientFactory.GetDatabase(client, options).GetCollection<BsonDocument>(MongoCollections.AuditLog);

        var committed = await runner.RunAsync(async ct =>
        {
            var inserted = (await store.InsertAsync(Anna("Blijft"), Now, ct)).AsT0;
            await recorder.RecordAsync(Entry(inserted.Id), ct);
            return TransactionOutcome.Commit(inserted.Id);
        }, Ct);
        var rolledBack = await runner.RunAsync(async ct =>
        {
            var inserted = (await store.InsertAsync(Anna("Weg"), Now, ct)).AsT0;
            await recorder.RecordAsync(Entry(inserted.Id), ct);
            return TransactionOutcome.Abort(inserted.Id);
        }, Ct);

        committed.IsT0.Should().BeTrue();
        rolledBack.IsT0.Should().BeTrue();
        (await users.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct)).Select(d => d["name"].AsString).Should().Equal("Blijft");
        (await audit.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
    }

    private static AuditEntry Entry(string userId) => new(
        AuditActor.System, AuditEntity.User, userId, AuditAction.Create, AuditObject.Empty, AuditObject.Of(("name", "x")));

    // ---- failure ----------------------------------------------------------------------------

    [Fact]
    public async Task EveryOperation_withUnreachableDatabase_isAValueFreePortError()
    {
        var unreachable = new MongoOptions { ConnectionString = "mongodb://127.0.0.1:1/x?serverSelectionTimeoutMS=300", DatabaseName = "x" };
        using var deadClient = new MongoClient(MongoClientSettings.FromConnectionString(unreachable.ConnectionString));
        var dead = new MongoUserStore(deadClient, unreachable);
        const string id = "0123456789abcdef01234567";

        var results = new[]
        {
            (await dead.ListAsync(new UserQuery(null, null, 1), Ct)).Match(_ => "", e => e.Message),
            (await dead.FindAsync(id, Ct)).Match(_ => "", _ => "", e => e.Message),
            (await dead.CountAsync(Ct)).Match(_ => "", e => e.Message),
            (await dead.CountOtherActiveAdminsAsync(id, Ct)).Match(_ => "", e => e.Message),
            (await dead.InsertAsync(Anna(), Now, Ct)).Match(_ => "", e => e.Message),
            (await dead.UpdateAsync(id, new UserPatch(Name: "X"), Now, Ct)).Match(_ => "", _ => "", e => e.Message, _ => ""),
        };

        results.Should().OnlyContain(m => m.StartsWith("users.", StringComparison.Ordinal) && !m.Contains("127.0.0.1", StringComparison.Ordinal));
    }
}
