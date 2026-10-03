using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Host.Audit;
using Huishoudplanner.Host.Configuration;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using OneOf;

namespace Huishoudplanner.Integration.Tests;

/// <summary>
/// Ports apps/server/test/audit-retention.test.ts onto <see cref="IAuditRetentionService"/> with a fixed clock and a real
/// MongoDB replica set (the manual trigger, <c>POST /api/jobs/audit-retention</c>, belongs to the jobs slice).
/// </summary>
public sealed class AuditRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 3, 0, 0, TimeSpan.Zero);

    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private readonly MongoClient mongoClient;
    private readonly IMongoDatabase database;
    private readonly MongoContainerFixture mongo;
    private readonly List<IDisposable> disposables = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AuditRetentionTests(MongoContainerFixture mongo)
    {
        this.mongo = mongo;
        mongoClient = new MongoClient(mongo.ConnectionString);
        database = mongoClient.GetDatabase(databaseName);
    }

    public void Dispose()
    {
        disposables.ForEach(d => d.Dispose());
        mongoClient.DropDatabase(databaseName);
        mongoClient.Dispose();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Retention(int? days) : ForReadingAuditRetention
    {
        public Task<OneOf<AuditRetentionPolicy, PortError>> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<OneOf<AuditRetentionPolicy, PortError>>(new AuditRetentionPolicy(days));
    }

    private IAuditRetentionService Service(int? days)
    {
        var factory = ApiFactory.ForMongo(mongo, databaseName).WithoutSeeding()
            .WithPort<ForReadingAuditRetention>(new Retention(days))
            .WithPort<TimeProvider>(new FixedClock(Now));
        disposables.Add(factory);
        _ = factory.CreateClient();
        var scope = factory.Services.CreateScope();
        disposables.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IAuditRetentionService>();
    }

    private IMongoCollection<BsonDocument> AuditLog => database.GetCollection<BsonDocument>("auditLog");

    private async Task SeedAsync()
    {
        foreach (var (label, age) in new[] { ("old", 31), ("exactly-at-cutoff", 30), ("recent", 29), ("today", 0) })
        {
            await AuditLog.InsertOneAsync(
                new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() },
                    { "at", new BsonDateTime(Now.AddDays(-age).UtcDateTime) },
                    { "actorId", ObjectId.Parse("000000000000000000000000") },
                    { "entity", "task" },
                    { "entityId", ObjectId.GenerateNewId() },
                    { "action", "update" },
                    { "before", new BsonDocument() },
                    { "after", new BsonDocument("label", label) },
                    { "source", "system" },
                },
                cancellationToken: Ct);
        }
    }

    private async Task<string[]> RemainingLabels() =>
        [.. (await AuditLog.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("at")).ToListAsync(Ct))
            .Select(d => d["after"]["label"].AsString)];

    [Fact]
    public async Task Run_deletesOnlyEntriesOlderThanTheRetention()
    {
        await SeedAsync();

        var result = await Service(30).RunAsync(Ct);

        result.AsT1.Should().Be(new RetentionDone(Now.AddDays(-30), 1));
        (await RemainingLabels()).Should().Equal("exactly-at-cutoff", "recent", "today");
    }

    [Fact]
    public async Task Run_whenRetentionIsNotConfigured_isDisabled_andDeletesNothing()
    {
        await SeedAsync();

        var result = await Service(null).RunAsync(Ct);

        result.IsT0.Should().BeTrue();
        (await RemainingLabels()).Should().Equal("old", "exactly-at-cutoff", "recent", "today");
    }

    [Fact]
    public async Task Run_isIdempotent_andWritesNoAuditEntry()
    {
        await SeedAsync();
        var service = Service(30);

        await service.RunAsync(Ct);
        var second = await service.RunAsync(Ct);

        second.AsT1.Deleted.Should().Be(0);
        (await RemainingLabels()).Should().Equal("exactly-at-cutoff", "recent", "today");
    }

    [Fact]
    public async Task Run_touchesNothingButTheAuditLog()
    {
        await SeedAsync();
        await database.GetCollection<BsonDocument>("rooms").InsertOneAsync(new BsonDocument("name", "Keuken"), cancellationToken: Ct);

        await Service(1).RunAsync(Ct);

        (await database.GetCollection<BsonDocument>("rooms").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct)).Should().Be(1);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData(null, null)]
    public async Task TheRetentionComesFromTheConfiguration_AUDIT_RETENTION_DAYS(string? value, int? expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MONGO_URL"] = "mongodb://localhost:27017/x", ["AUDIT_RETENTION_DAYS"] = value })
            .Build();
        using var provider = new ServiceCollection().AddAppOptions(configuration).AddAuditLog().BuildServiceProvider();

        var policy = await provider.GetRequiredService<ForReadingAuditRetention>().GetAsync(Ct);

        policy.AsT0.Days.Should().Be(expected);
    }
}
