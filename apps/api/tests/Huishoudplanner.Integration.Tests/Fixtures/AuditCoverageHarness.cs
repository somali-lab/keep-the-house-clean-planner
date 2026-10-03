using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Integration.Tests.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Huishoudplanner.Integration.Tests.Fixtures;

/// <summary>
/// The real host on a real MongoDB replica set with a <see cref="WriteCapture"/> on its client, arranged as the household of the Node
/// <c>write-routes-coverage.test.ts</c>: two people, an administrator, a planner, a room, two tasks, the active plan with slots and the
/// generated occurrences of the first two cycles, a fixed clock (Wednesday 2026-09-16 10:00 Amsterdam) and a deterministic mock model for
/// the AI endpoints. One harness serves one test class; the scenarios arrange their own data on top of it.
/// </summary>
public sealed class AuditCoverageHarness : IAsyncLifetime
{
    private const string Monday = "2026-09-14T06:00:00.000Z";
    private const string Wednesday = "2026-09-16T08:00:00.000Z";

    private readonly MongoContainerFixture mongo;
    private readonly FakeUserDirectory directory = new();
    private readonly string databaseName = MongoContainerFixture.NewDatabaseName();
    private MongoClient? mongoClient;
    private ApiFactory? factory;

    public AuditCoverageHarness(MongoContainerFixture mongo)
    {
        this.mongo = mongo;
        Clock = new FixedTimeProvider(Monday);
    }

    public FixedTimeProvider Clock { get; }

    public WriteCapture Capture { get; } = new();

    /// <summary>Extra configuration of the host (for example a test-only endpoint); set before <see cref="InitializeAsync"/>.</summary>
    public Func<ApiFactory, ApiFactory>? Configure { get; init; }

    public UserIdentity P1 { get; private set; } = null!;

    public UserIdentity P2 { get; private set; } = null!;

    public UserIdentity Admin { get; private set; } = null!;

    public UserIdentity Planner { get; private set; } = null!;

    public string Room { get; private set; } = null!;

    public string Weekly { get; private set; } = null!;

    public string Twice { get; private set; } = null!;

    public string ActivePlan { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>Every route of the real host, to compare with the scenarios.</summary>
    public IEnumerable<RouteEndpoint> Endpoints => factory!.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    public IMongoDatabase Database => mongoClient!.GetDatabase(databaseName);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        P1 = directory.Add(Role.Member);
        P2 = directory.Add(Role.Member);
        Admin = directory.Add(Role.Admin);
        Planner = directory.Add(Role.Planner);
        mongoClient = new MongoClient(mongo.ConnectionString);
        factory = ApiFactory.ForMongo(mongo, databaseName)
            .WithPort<ForFindingUsers>(directory)
            .WithPort<TimeProvider>(Clock)
            .WithPort<ForSelectingAModel>(new TestModels(null))
            .WithWriteCapture(Capture);
        factory = Configure?.Invoke(factory) ?? factory;
        Client = factory.CreateClient();

        await SeedPersonAsync(P1, "Persoon 1", []);
        await SeedPersonAsync(P2, "Persoon 2", [2]);
        await SeedPersonAsync(Admin, "Beheerder", []);
        Room = await SeedRoomAsync("Badkamer");
        Weekly = await CreatedAsync("/api/v2/tasks", new { name = "Badkamer schoonmaken", roomId = Room, intervalKey = "1w", durationMinutes = 30 });
        Twice = await CreatedAsync("/api/v2/tasks", new { name = "Wastafel", roomId = Room, intervalKey = "2w", durationMinutes = 10 });
        ActivePlan = (await SendAsync(HttpMethod.Get, "/api/v2/cycle-plans/active", null, null)).Body.GetProperty("id").GetString()!;
        var slots = new List<object>();
        foreach (var week in Enumerable.Range(0, 4))
        {
            slots.Add(new { taskId = Weekly, weekIndex = week, weekday = 1, assigneeId = P1.Id });
            slots.Add(new { taskId = Twice, weekIndex = week, weekday = 3, assigneeId = (string?)null });
            slots.Add(new { taskId = Twice, weekIndex = week, weekday = 4, assigneeId = P2.Id });
        }

        var put = await SendAsync(HttpMethod.Put, $"/api/v2/cycle-plans/{ActivePlan}/slots", new { slots }, Planner);
        put.Status.Should().Be(HttpStatusCode.OK, put.Body.ToString());
        using (var scope = factory.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<IGenerationService>().GenerateUpcomingAsync(AuditActor.System, "audit-coverage-harness", Ct);
            run.IsT0.Should().BeTrue();
        }

        Clock.Set(Wednesday);
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        factory?.Dispose();
        mongoClient?.DropDatabase(databaseName);
        mongoClient?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends a request as <paramref name="actor"/> (no profile header when <see langword="null"/>); a <see langword="null"/> body sends none.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string url, object? body, UserIdentity? actor)
    {
        using var request = new HttpRequestMessage(method, url);
        if (actor is not null)
        {
            request.Headers.Add("X-Profile-Id", actor.Id);
        }

        request.Headers.Add("X-Client", "web");
        if (body is not null)
        {
            request.Content = body is string raw ? new StringContent(raw, Encoding.UTF8, "application/json") : JsonContent.Create(body);
        }

        using var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>The id of a generated occurrence of a task on a day, from the list endpoint.</summary>
    public async Task<string> OccurrenceIdAsync(string taskId, string day)
    {
        var list = await SendAsync(HttpMethod.Get, $"/api/v2/occurrences?from={day}&to={day}", null, null);
        list.Status.Should().Be(HttpStatusCode.OK, list.Body.ToString());
        return list.Body.GetProperty("items").EnumerateArray().First(o => o.GetProperty("taskId").GetString() == taskId).GetProperty("id").GetString()!;
    }

    /// <summary>Removes the generated occurrences so the generation job has work to repair (arranged below the application, like the Node test).</summary>
    public async Task DeleteGeneratedOccurrencesAsync() =>
        await Database.GetCollection<BsonDocument>("occurrences").DeleteManyAsync(new BsonDocument("origin", "generated"), Ct);

    public async Task<HashSet<ObjectId>> AuditIdsAsync() =>
        (await Database.GetCollection<BsonDocument>("auditLog").Find(FilterDefinition<BsonDocument>.Empty).Project(new BsonDocument("_id", 1)).ToListAsync(Ct))
            .Select(d => d["_id"].AsObjectId).ToHashSet();

    public async Task<List<BsonDocument>> AuditEntriesExceptAsync(HashSet<ObjectId> known) =>
        (await Database.GetCollection<BsonDocument>("auditLog").Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(Ct))
            .Where(d => !known.Contains(d["_id"].AsObjectId)).ToList();

    /// <summary>Drift only a reconciliation repairs: an execution ledger entry whose occurrence does not exist (written below the application, like the Node test).</summary>
    public async Task InsertStrayLedgerEntryAsync()
    {
        var now = new BsonDateTime(DateTime.Parse(Wednesday, CultureInfo.InvariantCulture).ToUniversalTime());
        var day = new BsonDateTime(DateTime.Parse(Monday, CultureInfo.InvariantCulture).ToUniversalTime().Date);
        await Database.GetCollection<BsonDocument>("pointEntries").InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "key", "execution:" + ObjectId.GenerateNewId() }, { "kind", "execution" }, { "personId", ObjectId.Parse(P1.Id) },
                { "amount", 4 }, { "date", day }, { "weekStart", day }, { "occurrenceId", ObjectId.GenerateNewId() }, { "taskId", BsonNull.Value },
                { "titleSnapshot", "Verdwaald" }, { "source", "live" }, { "createdAt", now }, { "updatedAt", now },
            },
            cancellationToken: Ct);
    }

    private async Task<string> CreatedAsync(string url, object body)
    {
        var response = await SendAsync(HttpMethod.Post, url, body, Planner);
        response.Status.Should().Be(HttpStatusCode.Created, response.Body.ToString());
        return response.Body.GetProperty("id").GetString()!;
    }

    private async Task SeedPersonAsync(UserIdentity identity, string name, int[] unavailable)
    {
        var at = new BsonDateTime(DateTime.Parse(Monday, CultureInfo.InvariantCulture).ToUniversalTime().AddDays(-30));
        await Database.GetCollection<BsonDocument>("users").InsertOneAsync(
            new BsonDocument
            {
                { "_id", ObjectId.Parse(identity.Id) }, { "name", name }, { "color", "#336699" }, { "active", true },
                { "role", identity == Admin ? "admin" : "member" },
                { "unavailableWeekdays", new BsonArray(unavailable) },
                { "dailyBudgetMinutes", new BsonDocument { { "weekday", 600 }, { "weekend", 1200 } } },
                { "maxDailyMinutes", new BsonDocument { { "weekday", 1200 }, { "weekend", 2400 } } },
                { "createdAt", at }, { "updatedAt", at },
            },
            cancellationToken: Ct);
    }

    private async Task<string> SeedRoomAsync(string name)
    {
        var id = ObjectId.GenerateNewId();
        var at = new BsonDateTime(DateTime.Parse(Monday, CultureInfo.InvariantCulture).ToUniversalTime().AddDays(-30));
        await Database.GetCollection<BsonDocument>("rooms").InsertOneAsync(
            new BsonDocument { { "_id", id }, { "name", name }, { "sortOrder", 10 }, { "active", true }, { "virtual", false }, { "createdAt", at }, { "updatedAt", at } },
            cancellationToken: Ct);
        return id.ToString();
    }
}
