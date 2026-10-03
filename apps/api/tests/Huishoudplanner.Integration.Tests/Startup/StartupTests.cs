using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Host.Startup;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Integration.Tests.Startup;

/// <summary>
/// The startup order (migrations, then indexes, then the seed) and the seeding from <c>SEED_USERS</c>: the users part of
/// apps/server/test/seed.test.ts through the real host. The settings, rooms and plan parts of the Node seed arrive with their slices.
/// </summary>
public sealed class StartupTests(MongoContainerFixture mongo)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the order, with fakes --------------------------------------------------------------

    private sealed class RecordingStorage(List<string> log, PortError? failure = null) : ForPreparingStorage
    {
        public Task<OneOf<Success, PortError>> PrepareAsync(CancellationToken cancellationToken)
        {
            log.Add("storage");
            return Task.FromResult<OneOf<Success, PortError>>(failure is null ? new Success() : failure);
        }
    }

    private sealed class RecordingSeed(List<string> log, string name, bool fail = false) : ISeedStep
    {
        public string Name => name;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            log.Add(name);
            return fail ? throw new InvalidOperationException("seed failed") : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Start_preparesTheStorageFirst_thenRunsTheSeedStepsInOrder()
    {
        var log = new List<string>();
        var service = new StartupService(new RecordingStorage(log), [new RecordingSeed(log, "users"), new RecordingSeed(log, "rooms")], NullLogger<StartupService>.Instance);

        await service.StartAsync(Ct);

        log.Should().Equal("storage", "users", "rooms");
    }

    [Fact]
    public async Task Start_whenTheStorageCannotBePrepared_failsBeforeAnySeed_withAValueFreeMessage()
    {
        var log = new List<string>();
        var service = new StartupService(
            new RecordingStorage(log, new PortError("storage.prepare: migrations or indexes failed (MongoConnectionException).")),
            [new RecordingSeed(log, "users")],
            NullLogger<StartupService>.Instance);

        var start = async () => await service.StartAsync(Ct);

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*storage.prepare*");
        log.Should().Equal("storage");
    }

    [Fact]
    public async Task Start_aFailingSeedStep_stopsTheStart()
    {
        var log = new List<string>();
        var service = new StartupService(
            new RecordingStorage(log), [new RecordingSeed(log, "users", fail: true), new RecordingSeed(log, "rooms")], NullLogger<StartupService>.Instance);

        var start = async () => await service.StartAsync(Ct);

        await start.Should().ThrowAsync<InvalidOperationException>();
        log.Should().Equal("storage", "users");
    }

    // ---- the real host ----------------------------------------------------------------------

    [Fact]
    public async Task Host_onAnEmptyDatabase_seedsThePersonenAsAdminAndMember_auditedAsTheSystem()
    {
        await using var host = await UsersHost.StartAsync(mongo);

        var users = await host.Users.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);
        users.Select(u => (u["name"].AsString, u["color"].AsString, u["role"].AsString)).Should().Equal(
            ("Persoon 1", "#2563eb", "admin"),
            ("Persoon 2", "#db2777", "member"));
        users.Should().OnlyContain(u => u["dailyBudgetMinutes"]["weekday"].AsInt32 == 60 && u["dailyBudgetMinutes"]["weekend"].AsInt32 == 120);
        var audit = await host.AuditSince();
        audit.Should().HaveCount(2);
        audit.Should().OnlyContain(e =>
            e["source"].AsString == "system" &&
            e["actorId"].AsObjectId == ObjectId.Empty &&
            e["entity"].AsString == "user" &&
            e["action"].AsString == "create");
        audit.Select(e => e["entityId"].AsObjectId).Should().BeEquivalentTo(users.Select(u => u["_id"].AsObjectId));
        users[0]["createdAt"].ToUniversalTime().Should().Be(UsersHost.Now.UtcDateTime);
    }

    [Fact]
    public async Task Host_seedsAConfigurableNumberOfUsersFromSeedUsers()
    {
        const string seedUsers = """[{"name":"Anna","color":"#111111"},{"name":"Bram","color":"#222222"},{"name":"Chris","color":"#333333"}]""";
        await using var host = await UsersHost.StartAsync(mongo, f => f.WithSetting("SEED_USERS", seedUsers));

        var users = await host.Users.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct);

        users.Select(u => u["name"].AsString).Should().Equal("Anna", "Bram", "Chris");
        users.Select(u => u["role"].AsString).Should().Equal("admin", "member", "member");
    }

    [Fact]
    public async Task Host_startedAgainOnTheSameDatabase_isIdempotent_noDuplicatesAndNoExtraAudit()
    {
        var databaseName = MongoContainerFixture.NewDatabaseName();
        using var client = new MongoClient(mongo.ConnectionString);
        var database = client.GetDatabase(databaseName);
        try
        {
            await using (var first = ApiFactory.ForMongo(mongo, databaseName))
            {
                using var http = first.CreateClient();
            }

            var users = await database.GetCollection<BsonDocument>("users").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
            var audit = await database.GetCollection<BsonDocument>("auditLog").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

            await using (var second = ApiFactory.ForMongo(mongo, databaseName))
            {
                using var http = second.CreateClient();
            }

            (await database.GetCollection<BsonDocument>("users").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(users).And.Be(2);
            (await database.GetCollection<BsonDocument>("auditLog").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(audit).And.Be(2);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, Ct);
        }
    }

    [Fact]
    public async Task Host_doesNotAddUsersWhenSomeAlreadyExist()
    {
        var databaseName = MongoContainerFixture.NewDatabaseName();
        using var client = new MongoClient(mongo.ConnectionString);
        var database = client.GetDatabase(databaseName);
        try
        {
            await database.GetCollection<BsonDocument>("users").InsertOneAsync(
                new BsonDocument { { "name", "Solo" }, { "active", true }, { "role", "admin" } }, cancellationToken: Ct);

            await using (var factory = ApiFactory.ForMongo(mongo, databaseName))
            {
                using var http = factory.CreateClient();
            }

            var names = await database.GetCollection<BsonDocument>("users").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);
            names.Select(u => u["name"].AsString).Should().Equal("Solo");
            (await database.GetCollection<BsonDocument>("auditLog").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(0);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, Ct);
        }
    }

    [Fact]
    public async Task Host_runsTheMigrationsBeforeTheIndexes_soTheLegacySlotIndexNeverConflicts()
    {
        var databaseName = MongoContainerFixture.NewDatabaseName();
        using var client = new MongoClient(mongo.ConnectionString);
        var database = client.GetDatabase(databaseName);
        try
        {
            // An installation from before ADR-0009: unique over every occurrence, not only the generated ones.
            var occurrences = database.GetCollection<BsonDocument>("occurrences");
            await occurrences.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(new BsonDocument { { "cycleId", 1 }, { "taskId", 1 }, { "plannedDate", 1 } }, new CreateIndexOptions { Unique = true }),
                cancellationToken: Ct);

            await using (var factory = ApiFactory.ForMongo(mongo, databaseName))
            {
                using var http = factory.CreateClient();
            }

            var indexes = (await (await occurrences.Indexes.ListAsync(Ct)).ToListAsync(Ct)).Select(i => i["name"].AsString).ToList();
            indexes.Should().Contain("occurrences_generated_slot_unique").And.NotContain("cycleId_1_taskId_1_plannedDate_1");
            var migrations = await database.GetCollection<BsonDocument>("migrations").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);
            migrations.Select(m => m["name"].AsString).Should().Contain("001-drop-legacy-generated-slot-index");
            (await database.GetCollection<BsonDocument>("users").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(2);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName, Ct);
        }
    }
}
